using System;
using System.Collections.Generic;
using System.Linq;
using Evergine.Common.Input.Keyboard;
using Evergine.Common.Input.Mouse;
using Evergine.Components.Cameras;
using Evergine.Framework;
using Evergine.Framework.Graphics;
using Evergine.Framework.Physics;
using Evergine.Mathematics;

namespace PhysicSample.Components
{
    /// <summary>
    /// What the driver is asking the forklift to do. Steer is -1 (left) .. 1 (right), Lift is -1 (down) .. 1 (up),
    /// Tilt is -1 (back) .. 1 (forward).
    /// </summary>
    public struct ForkliftInput
    {
        public float Throttle;
        public float Steer;
        public float Brake;
        public float HandBrake;
        public float Lift;
        public float Tilt;
        public float Spread;
    }

    /// <summary>
    /// The driver of the forklift. All the physics (chassis body, colliders, vehicle, wheels, mast bodies and their
    /// hinge and sliders) is authored in the scene; this only reads the keyboard, works the pedals, the steering and
    /// the hydraulic valves, and moves the camera.
    /// </summary>
    /// <remarks>
    /// Lives on the entity that groups the chassis and the mast parts. Controls: W/S or Up/Down drive, brake and
    /// reverse; A/D or Left/Right steer; Space parking brake; E/Q or PageUp/PageDown lift and lower; R/F or Home/End
    /// tilt forward and back; X/Z spread and close the forks; C goes through the chase, driver, fork and free cameras;
    /// the right mouse button turns the view, the wheel zooms and the middle button resets it.
    /// </remarks>
    public class ForkliftVehicle : Behavior
    {
        private enum CameraView
        {
            Chase,
            Driver,
            Forks,
            Free,
        }

        /// <summary>
        /// The mouse turn and zoom of a view: the values the camera eases towards and the values it shows.
        /// </summary>
        private struct Orbit
        {
            public float Yaw;
            public float Pitch;
            public float Zoom;
            public float TargetYaw;
            public float TargetPitch;
            public float TargetZoom;
        }

        private bool found;
        private PhysicsManager physicsManager;
        private Transform3D chassis;
        private RigidBody body;
        private WheeledVehicleController vehicle;
        private HingeConstraint tiltHinge;
        private SliderConstraint mastSlider;
        private SliderConstraint carriageSlider;
        private Transform3D steeringWheel;
        private Quaternion steeringWheelRest;
        private Transform3D[] forks = Array.Empty<Transform3D>();
        private float forkSpacing;
        private Transform3D[] wheelVisuals = Array.Empty<Transform3D>();
        private int framesUntilWheelCheck = 3;

        private Camera3D camera;
        private FreeCamera3D freeCamera;
        private Transform3D driverAnchor;
        private Transform3D forkAnchor;
        private float chaseFieldOfView;
        private float driverFieldOfView;
        private float forkFieldOfView;
        private CameraView view = CameraView.Chase;
        private Orbit[] orbits = new Orbit[3];
        private bool cameraKeyWasDown;
        private bool cameraCut = true;
        private HashSet<PhysicsBody> truckBodies;
        private Vector3 cameraFocus;
        private Vector3 cameraOffset;

        // What the on-screen touch controls ask for, set every frame by TouchControls.
        private ForkliftInput touchInput;
        private bool touchLookHeld;
        private float touchYaw;
        private float touchPitch;
        private float touchZoom = 1f;
        private bool touchReset;

        private float steer;
        private VehicleWheel[] steeredWheels = Array.Empty<VehicleWheel>();
        private float[] steerLocks = Array.Empty<float>();
        private float wheelbase;
        private float truckMass;
        private float pedal;

        /// <summary>Gets or sets the speed the governor holds the truck to, in kilometers per hour.</summary>
        public float TopSpeedKmh { get; set; } = 18f;

        /// <summary>
        /// Gets or sets the share of the stall torque left at top speed. A torque converter multiplies the engine torque
        /// by some 2.5 at stall and by about 1 once coupled, so with the vehicle's EngineMaxTorque set to the stall
        /// torque this is roughly the inverse of that multiplication.
        /// </summary>
        public float ConverterCoupledShare { get; set; } = 0.4f;

        /// <summary>Gets or sets how long the engine takes to answer a fully pressed pedal, in seconds.</summary>
        public float ThrottleRiseTime { get; set; } = 0.6f;

        /// <summary>Gets or sets how fast the steering reaches full lock, in full turns of the input per second.</summary>
        public float SteerSpeed { get; set; } = 2.5f;

        /// <summary>Gets or sets how fast the forks lift, in meters per second.</summary>
        public float LiftSpeed { get; set; } = 0.55f;

        /// <summary>Gets or sets how fast the forks lower, in meters per second.</summary>
        public float LowerSpeed { get; set; } = 0.5f;

        /// <summary>Gets or sets how fast the mast tilts, in degrees per second.</summary>
        public float TiltSpeed { get; set; } = 4f;

        /// <summary>
        /// Gets or sets how hard the truck slows down when the pedal is released, in m/s^2. Jolt's automatic gearbox
        /// drops to neutral without throttle and its tyres have no rolling resistance, so the truck would coast freely;
        /// a real one is held back by the torque converter, engine braking and its heavily loaded tyres.
        /// </summary>
        public float CoastDeceleration { get; set; } = 0.8f;

        /// <summary>
        /// Gets or sets the speed below which, with no pedal pressed, the brakes hold the truck still, in km/h. Without
        /// it the free rolling wheels let the slightest push (the mast moving, a bump) make the truck drift.
        /// </summary>
        public float HoldBelowKmh { get; set; } = 1f;

        /// <summary>Gets the name of the camera view in use: Chase, Driver, Forks or Free.</summary>
        public string CameraViewName => this.view.ToString();

        /// <summary>Gets or sets how close the chase and fork cameras may come to walls, racks and floor, in meters.</summary>
        public float CameraRadius { get; set; } = 0.2f;

        /// <summary>Gets or sets a value indicating whether the camera follows the truck when the scene starts.</summary>
        public bool FollowCamera { get; set; } = true;

        /// <summary>Gets or sets how fast the fork positioner changes the spacing of the forks, in meters per second.</summary>
        public float ForkSpreadSpeed { get; set; } = 0.2f;

        /// <summary>Gets or sets the closest the forks can come, centre to centre, in meters.</summary>
        public float MinForkSpacing { get; set; } = 0.24f;

        /// <summary>
        /// Gets or sets the widest the forks can open, centre to centre, in meters. The SimReady forklift lets each fork
        /// slide out to 0.46 m from the middle of the carriage.
        /// </summary>
        public float MaxForkSpacing { get; set; } = 0.92f;

        // Speed along the nose of the truck, in meters per second. Negative is reversing.
        private float ForwardSpeed => this.body == null
            ? 0f
            : Vector3.Dot(this.body.LinearVelocity, Vector3.TransformNormal(Vector3.UnitZ, this.chassis.WorldTransform));

        /// <inheritdoc/>
        protected override void OnActivated()
        {
            base.OnActivated();
            this.view = this.FollowCamera ? CameraView.Chase : CameraView.Free;
        }

        /// <inheritdoc/>
        protected override void OnDeactivated()
        {
            if (this.physicsManager != null)
            {
                this.physicsManager.PhysicsStepStarting -= this.OnPhysicsStepStarting;
            }

            this.found = false;
            base.OnDeactivated();
        }

        /// <inheritdoc/>
        protected override void Update(TimeSpan gameTime)
        {
            if (!this.found && !this.FindParts())
            {
                return;
            }

            this.CorrectWheelFacing();
            this.UpdateSteeringWheel();
            this.UpdateCamera((float)gameTime.TotalSeconds);
        }

        private bool FindParts()
        {
            this.vehicle = this.Owner.FindComponentInChildren<WheeledVehicleController>();
            this.tiltHinge = this.Owner.FindComponentInChildren<HingeConstraint>();
            SliderConstraint[] sliders = this.Owner.FindComponentsInChildren<SliderConstraint>().ToArray();
            this.physicsManager = this.Managers.FindManager<PhysicsManager>();

            if (this.vehicle == null || this.tiltHinge == null || sliders.Length != 2 || this.physicsManager == null)
            {
                return false;
            }

            // The carriage slides on the inner mast, which itself slides on the outer one.
            this.carriageSlider = sliders.First(s => s.ConnectedEntity?.FindComponent<SliderConstraint>() != null);
            this.mastSlider = sliders.First(s => s != this.carriageSlider);

            this.chassis = this.vehicle.Owner.FindComponent<Transform3D>();
            this.body = this.vehicle.Owner.FindComponent<RigidBody>();
            this.steeringWheel = this.vehicle.Owner.ChildEntities
                .FirstOrDefault(e => e.Name.StartsWith("sm_steering_wheel", StringComparison.Ordinal))?.FindComponent<Transform3D>();
            this.steeringWheelRest = this.steeringWheel?.LocalOrientation ?? Quaternion.Identity;
            this.truckMass = this.Owner.FindComponentsInChildren<RigidBody>().Sum(b => b.Mass);

            // The forks are the children of the carriage named sm_fork, one either side of its middle.
            this.forks = this.carriageSlider.Owner.ChildEntities
                .Where(e => e.Name.StartsWith("sm_fork", StringComparison.Ordinal))
                .Select(e => e.FindComponent<Transform3D>())
                .ToArray();
            this.forkSpacing = this.forks.Length == 2 ? Math.Abs(this.forks[0].LocalPosition.X - this.forks[1].LocalPosition.X) : 0f;

            this.physicsManager.PhysicsStepStarting += this.OnPhysicsStepStarting;
            this.found = true;

            return true;
        }

        /// <summary>
        /// Feeds the driver and the hydraulics inside the fixed step, so the truck behaves the same at any frame rate.
        /// </summary>
        private void OnPhysicsStepStarting(object sender, float fixedTimeStep)
        {
            ForkliftInput input = Combine(this.ReadKeyboard(), this.touchInput);

            float speed = this.ForwardSpeed;
            float throttle = input.Throttle;
            float brake = input.Brake;

            // Pressing against the way the truck rolls brakes first, and only reverses once it has nearly stopped.
            if ((throttle < 0f && speed > 0.4f) || (throttle > 0f && speed < -0.4f))
            {
                brake = Math.Max(brake, Math.Abs(throttle));
                throttle = 0f;
            }

            // The engine takes a moment to pick up revs: the pedal reaches the engine progressively, released at once.
            if (throttle == 0f || Math.Sign(throttle) != Math.Sign(this.pedal))
            {
                this.pedal = 0f;
            }

            float pedalStep = fixedTimeStep / Math.Max(this.ThrottleRiseTime, 1e-3f);
            this.pedal = Math.Sign(throttle) * Math.Min(Math.Abs(this.pedal) + pedalStep, Math.Abs(throttle));
            throttle = this.pedal;

            // Torque converter: EngineMaxTorque on the vehicle is the torque at stall, the engine's own multiplied by the
            // converter, which is what gives the truck its pull at low speed. The multiplication fades as the truck
            // speeds up, down to ConverterCoupledShare of it at top speed, where the converter is all but coupled.
            float speedShare = Math.Clamp(Math.Abs(speed) * 3.6f / this.TopSpeedKmh, 0f, 1f);
            throttle *= MathHelper.Lerp(1f, this.ConverterCoupledShare, speedShare);

            // Speed governor: the throttle fades out over the last 1.5 km/h below the limit.
            throttle *= Math.Clamp((this.TopSpeedKmh - (Math.Abs(speed) * 3.6f)) / 1.5f, 0f, 1f);

            // Pedal released: stopped, the brakes hold the truck; rolling, they stand in for the drag of the drivetrain.
            if (input.Throttle == 0f)
            {
                bool stopped = this.body.LinearVelocity.Length() * 3.6f < this.HoldBelowKmh;
                brake = Math.Max(brake, stopped ? 1f : this.CoastBrake());

                if (stopped && input.Steer == 0f)
                {
                    // Static grip of braked tyres: Jolt's tyre model only finds lateral grip from the slip of a moving
                    // wheel, so at rest the truck creeps sideways under the push of the mast. Real tyres under four
                    // tonnes need some 30 kN to slide, so while held the truck does not move or turn on the ground;
                    // it still pitches and rolls on its tyres.
                    Vector3 linear = this.body.LinearVelocity;
                    Vector3 angular = this.body.AngularVelocity;
                    this.body.LinearVelocity = new Vector3(0f, linear.Y, 0f);
                    this.body.AngularVelocity = new Vector3(angular.X, 0f, angular.Z);
                }
            }

            // Full lock at speed would tip the truck over, so the lock available shrinks as it goes faster.
            float lockAtSpeed = MathHelper.Lerp(1f, 0.35f, Math.Clamp(Math.Abs(speed) / 5f, 0f, 1f));
            float target = Math.Clamp(input.Steer, -1f, 1f) * lockAtSpeed;
            float maxStep = this.SteerSpeed * fixedTimeStep;
            this.steer += Math.Clamp(target - this.steer, -maxStep, maxStep);

            // The steered wheels are at the back, so they turn the opposite way to front wheels for the same turn.
            this.SteerWithAckermann();
            this.vehicle.SetDriverInput(throttle, -this.steer, brake, input.HandBrake);

            this.DriveHydraulics(input, fixedTimeStep);
        }

        /// <summary>
        /// The brake input that slows the whole truck (chassis and mast) at <see cref="CoastDeceleration"/>, from the
        /// brake torque and radius of its wheels. A load on the forks is not counted, so a laden truck coasts further.
        /// </summary>
        /// <summary>
        /// Ackermann steering: in a turn the inner steered wheel turns further than the outer one, so both roll around
        /// the same centre, on the line of the drive axle. The vehicle turns both by the same angle, which in a tight
        /// turn drags one of them sideways; each wheel's lock is set so the steering input gives it its own angle.
        /// </summary>
        private void SteerWithAckermann()
        {
            if (this.steeredWheels.Length == 0)
            {
                // The wheels join the vehicle after it is found. The steered ones keep the lock authored in the scene as
                // the lock of the middle of their axle.
                this.steeredWheels = this.vehicle.Wheels.Where(w => w.MaxSteerAngle > 0f).ToArray();
                this.steerLocks = this.steeredWheels.Select(w => w.MaxSteerAngle).ToArray();
                VehicleWheel[] fixedWheels = this.vehicle.Wheels.Where(w => w.MaxSteerAngle == 0f).ToArray();
                this.wheelbase = this.steeredWheels.Length == 0 || fixedWheels.Length == 0 ? 0f : Math.Abs(
                    this.steeredWheels.Average(w => w.Owner.FindComponent<Transform3D>().LocalPosition.Z) -
                    fixedWheels.Average(w => w.Owner.FindComponent<Transform3D>().LocalPosition.Z));
            }

            for (int i = 0; i < this.steeredWheels.Length; i++)
            {
                VehicleWheel wheel = this.steeredWheels[i];
                float middleAngle = Math.Abs(this.steer) * this.steerLocks[i];

                if (middleAngle < 1e-3f || this.wheelbase <= 0f)
                {
                    wheel.MaxSteerAngle = this.steerLocks[i];
                    continue;
                }

                // The turn centre is on the side the truck turns to (right is -X), as far out as the middle of the axle
                // needs; the wheel nearer to it is the inner one.
                float radius = this.wheelbase / (float)Math.Tan(middleAngle);
                float centre = -Math.Sign(this.steer) * radius;
                float distance = Math.Abs(wheel.Owner.FindComponent<Transform3D>().LocalPosition.X - centre);
                wheel.MaxSteerAngle = (float)Math.Atan2(this.wheelbase, distance) / Math.Abs(this.steer);
            }
        }

        private float CoastBrake()
        {
            float force = 0f;

            foreach (VehicleWheel wheel in this.vehicle.Wheels)
            {
                force += wheel.MaxBrakeTorque / wheel.Radius;
            }

            return force > 0f ? Math.Clamp(this.truckMass * this.CoastDeceleration / force, 0f, 1f) : 0f;
        }

        /// <summary>
        /// The lift and tilt valves set how fast each cylinder moves; closed, the motor holds the cylinder where it is.
        /// </summary>
        private void DriveHydraulics(ForkliftInput input, float fixedTimeStep)
        {
            float lift = Math.Clamp(input.Lift, -1f, 1f);
            float carriageVelocity = 0f;
            float mastVelocity = 0f;

            // Going up the carriage climbs the inner mast first, then the inner mast rises; coming down, the reverse.
            if (lift > 0f)
            {
                if (this.carriageSlider.CurrentPosition < this.carriageSlider.MaxDistance - 0.005f)
                {
                    carriageVelocity = lift * this.LiftSpeed;
                }
                else
                {
                    mastVelocity = lift * this.LiftSpeed;
                }
            }
            else if (lift < 0f)
            {
                if (this.mastSlider.CurrentPosition > this.mastSlider.MinDistance + 0.005f)
                {
                    mastVelocity = lift * this.LowerSpeed;
                }
                else
                {
                    carriageVelocity = lift * this.LowerSpeed;
                }
            }

            this.carriageSlider.TargetVelocity = carriageVelocity;
            this.mastSlider.TargetVelocity = mastVelocity;
            this.tiltHinge.TargetAngularVelocity = Math.Clamp(input.Tilt, -1f, 1f) * MathHelper.ToRadians(this.TiltSpeed);

            if (lift != 0f || input.Tilt != 0f)
            {
                this.body.WakeUp();
            }

            this.DriveForkPositioner(input.Spread, fixedTimeStep);
        }

        /// <summary>
        /// The fork positioner slides both forks along the carriage, symmetrically about its middle. The fork colliders are
        /// part of the carriage body and follow their entities, so moving the entities moves what the forks push on.
        /// </summary>
        private void DriveForkPositioner(float spread, float fixedTimeStep)
        {
            if (spread == 0f || this.forks.Length != 2)
            {
                return;
            }

            this.forkSpacing = Math.Clamp(
                this.forkSpacing + (Math.Clamp(spread, -1f, 1f) * this.ForkSpreadSpeed * fixedTimeStep),
                this.MinForkSpacing,
                this.MaxForkSpacing);

            float middle = (this.forks[0].LocalPosition.X + this.forks[1].LocalPosition.X) * 0.5f;

            foreach (Transform3D fork in this.forks)
            {
                Vector3 position = fork.LocalPosition;
                position.X = middle + (Math.Sign(position.X - middle) * this.forkSpacing * 0.5f);
                fork.LocalPosition = position;
            }

            this.carriageSlider.Owner.FindComponent<RigidBody>()?.WakeUp();
        }

        /// <summary>Sets what the on-screen touch controls ask for; it adds to the keyboard, the stronger of the two wins.</summary>
        /// <param name="input">The driving and mast input.</param>
        public void SetTouchInput(ForkliftInput input) => this.touchInput = input;

        /// <summary>Turns the current view by a touch drag, as dragging with the right mouse button does.</summary>
        /// <param name="held">Whether a finger is looking around; while it is, the driver's head does not come back to the front.</param>
        /// <param name="yaw">The turn about the vertical, in radians.</param>
        /// <param name="pitch">The turn up or down, in radians.</param>
        public void TouchLook(bool held, float yaw, float pitch)
        {
            this.touchLookHeld = held;
            this.touchYaw += yaw;
            this.touchPitch += pitch;
        }

        /// <summary>Zooms the current view by a pinch: below 1 comes closer.</summary>
        /// <param name="factor">The zoom factor.</param>
        public void TouchZoom(float factor) => this.touchZoom *= factor;

        /// <summary>Brings the current view back to its default turn and zoom, like the middle mouse button.</summary>
        public void ResetView() => this.touchReset = true;

        /// <summary>Goes to the next camera view, like the C key.</summary>
        /// <param name="skipFree">Whether to skip the free camera, which flies with the keyboard only.</param>
        public void NextView(bool skipFree = false)
        {
            do
            {
                this.view = (CameraView)(((int)this.view + 1) % 4);
            }
            while ((this.view == CameraView.Driver && this.driverAnchor == null)
                || (this.view == CameraView.Forks && this.forkAnchor == null)
                || (this.view == CameraView.Free && skipFree));

            this.cameraCut = true;
        }

        private static ForkliftInput Combine(ForkliftInput a, ForkliftInput b)
        {
            static float Stronger(float x, float y) => Math.Abs(y) > Math.Abs(x) ? y : x;

            return new ForkliftInput
            {
                Throttle = Stronger(a.Throttle, b.Throttle),
                Steer = Stronger(a.Steer, b.Steer),
                Brake = Stronger(a.Brake, b.Brake),
                HandBrake = Stronger(a.HandBrake, b.HandBrake),
                Lift = Stronger(a.Lift, b.Lift),
                Tilt = Stronger(a.Tilt, b.Tilt),
                Spread = Stronger(a.Spread, b.Spread),
            };
        }

        private ForkliftInput ReadKeyboard()
        {
            ForkliftInput input = default;
            KeyboardDispatcher keyboard = this.Keyboard;

            if (keyboard == null)
            {
                return input;
            }

            // The free camera flies on WASD and Q/E, so the truck only listens to the other keys while it is active.
            bool letters = this.view != CameraView.Free;

            input.Throttle = Axis(keyboard, Keys.Up, Keys.Down, letters, Keys.W, Keys.S);
            input.Steer = Axis(keyboard, Keys.Right, Keys.Left, letters, Keys.D, Keys.A);
            input.Lift = Axis(keyboard, Keys.PageUp, Keys.PageDown, letters, Keys.E, Keys.Q);
            input.Tilt = Axis(keyboard, Keys.Home, Keys.End, letters, Keys.R, Keys.F);
            input.HandBrake = keyboard.IsKeyDown(Keys.Space) ? 1f : 0f;
            input.Spread = (keyboard.IsKeyDown(Keys.X) ? 1f : 0f) - (keyboard.IsKeyDown(Keys.Z) ? 1f : 0f);

            return input;
        }

        private static float Axis(KeyboardDispatcher keyboard, Keys positive, Keys negative, bool letters, Keys letterPositive, Keys letterNegative)
        {
            bool up = keyboard.IsKeyDown(positive) || (letters && keyboard.IsKeyDown(letterPositive));
            bool down = keyboard.IsKeyDown(negative) || (letters && keyboard.IsKeyDown(letterNegative));

            return (up ? 1f : 0f) - (down ? 1f : 0f);
        }

        private KeyboardDispatcher Keyboard => this.Managers?.RenderManager?.ActiveCamera3D?.Display?.KeyboardDispatcher;

        /// <summary>
        /// Evergine up to 2026.11.4 asks Jolt for the wheel pose with +X as the right axis of the wheel model, while
        /// Jolt derives it as forward x up (-X here), so every wheel comes back turned half a turn about up and shows
        /// its inner face. Each wheel mesh sits on a child entity of the wheel; one found facing the wrong way is
        /// turned back. Detected rather than assumed, so it does nothing once the package carries the fix.
        /// </summary>
        private void CorrectWheelFacing()
        {
            if (this.framesUntilWheelCheck <= 0 || this.vehicle.Wheels.Count == 0 || this.vehicle.Wheels[0].Index < 0 || --this.framesUntilWheelCheck > 0)
            {
                return;
            }

            this.wheelVisuals = this.vehicle.Wheels
                .Select(w => w.Owner.ChildEntities.FirstOrDefault()?.FindComponent<Transform3D>())
                .Where(t => t != null)
                .ToArray();

            Vector3 chassisAxle = Vector3.TransformNormal(Vector3.UnitX, this.chassis.WorldTransform);

            foreach (Transform3D visual in this.wheelVisuals)
            {
                Vector3 wheelAxle = Vector3.TransformNormal(Vector3.UnitX, visual.Owner.Parent.FindComponent<Transform3D>().WorldTransform);

                if (Vector3.Dot(wheelAxle, chassisAxle) < 0f)
                {
                    visual.LocalOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathHelper.Pi);
                }
            }
        }

        private void UpdateSteeringWheel()
        {
            if (this.steeringWheel != null)
            {
                // A few turns of the wheel from lock to lock, about the column (local Y).
                this.steeringWheel.LocalOrientation =
                    this.steeringWheelRest * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -this.steer * 2.5f);
            }
        }

        /// <summary>
        /// One camera, eased towards the view chosen with C: chase, driver's seat, fork side and free. The driver and fork
        /// views follow the DriverCamera and ForkCamera entities authored in the scene on the chassis and on the carriage;
        /// their disabled Camera3D only gives each view its field of view. Holding the right mouse button turns the view
        /// (orbits it, or turns the driver's head), the wheel zooms and the middle button resets it.
        /// </summary>
        private void UpdateCamera(float dt)
        {
            if (this.camera == null && !this.FindCameras())
            {
                return;
            }

            bool cameraKeyDown = this.Keyboard?.IsKeyDown(Keys.C) == true;

            if (cameraKeyDown && !this.cameraKeyWasDown)
            {
                this.NextView();
            }

            this.cameraKeyWasDown = cameraKeyDown;

            if (this.freeCamera != null)
            {
                this.freeCamera.IsEnabled = this.view == CameraView.Free;
            }

            if (this.view == CameraView.Free)
            {
                this.camera.FieldOfView = this.chaseFieldOfView;
                return;
            }

            ref Orbit orbit = ref this.orbits[(int)this.view];
            this.TurnWithMouse(ref orbit, dt);

            Transform3D transform = this.camera.Transform;

            if (this.view == CameraView.Driver)
            {
                // The eyes stay on the seat; the head turns about the truck's up and nods about its own sideways axis.
                Vector3 up = Vector3.TransformNormal(Vector3.UnitY, this.chassis.WorldTransform);
                Quaternion head = Quaternion.CreateFromAxisAngle(up, orbit.Yaw) * this.driverAnchor.Orientation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, orbit.Pitch);
                float headBlend = this.cameraCut ? 1f : 1f - (float)Math.Exp(-dt * 15f);
                transform.Position = this.driverAnchor.Position;
                transform.Orientation = Quaternion.Slerp(transform.Orientation, head, headBlend);
                this.camera.FieldOfView = this.driverFieldOfView * orbit.Zoom;
                this.cameraCut = false;
                return;
            }

            // What the camera looks at and where it sits from there before the mouse turns it. Only these follow the
            // truck with a lag, so bumps and the mast moving do not shake the view; the mouse orbit is applied on top
            // straight away, so the camera swings round the focus instead of drifting towards its new place.
            Vector3 focus;
            Vector3 offset;
            float follow;
            float minElevation;
            float fieldOfView;

            if (this.view == CameraView.Forks)
            {
                // Looks at a spot ahead of the forks; the camera can be swung round it to the other side.
                focus = this.forkAnchor.Position + (Vector3.Transform(-Vector3.UnitZ, this.forkAnchor.Orientation) * 2f);
                offset = this.forkAnchor.Position - focus;
                follow = 8f;
                minElevation = -30f;
                fieldOfView = this.forkFieldOfView;
            }
            else
            {
                // Behind and above the truck.
                Matrix4x4 world = this.chassis.WorldTransform;
                Vector3 nose = new Vector3(world.M31, 0f, world.M33);
                nose = nose.LengthSquared() > 1e-4f ? Vector3.Normalize(nose) : Vector3.UnitZ;
                focus = this.chassis.Position + new Vector3(0f, 1.2f, 0f);
                offset = (-nose * 6.5f) + new Vector3(0f, 2f, 0f);
                follow = 4f;
                minElevation = -5f;
                fieldOfView = this.chaseFieldOfView;
            }

            float blend = this.cameraCut ? 1f : 1f - (float)Math.Exp(-dt * follow);
            this.cameraFocus = Vector3.Lerp(this.cameraFocus, focus, blend);
            this.cameraOffset = Vector3.Lerp(this.cameraOffset, offset, blend);

            transform.Position = this.KeepClear(this.chassis.Position + new Vector3(0f, 1.2f, 0f), this.cameraFocus + OrbitOffset(this.cameraOffset, orbit, minElevation));
            transform.Orientation = LookRotation(transform.Position, this.cameraFocus);
            this.camera.FieldOfView = MathHelper.Lerp(this.camera.FieldOfView, fieldOfView, blend);
            this.cameraCut = false;
        }

        /// <summary>
        /// Keeps the camera on the near side of anything solid: a sphere the size of the camera is swept from the
        /// middle of the truck to where the camera wants to be, and the camera stops where the sphere first touches a
        /// body that is not part of the truck. Soft bodies, like strip curtains, are passed through: the camera would
        /// otherwise snag on the strips as they swing.
        /// </summary>
        private Vector3 KeepClear(Vector3 from, Vector3 camera)
        {
            this.truckBodies ??= this.Owner.FindComponentsInChildren<PhysicsBody>(isExactType: false).ToHashSet();

            Vector3 offset = camera - from;
            float distance = offset.Length();

            if (distance < 1e-3f || this.physicsManager == null)
            {
                return camera;
            }

            Vector3 direction = offset / distance;
            QueryFilter notTheTruck = new QueryFilter { Predicate = body => !this.truckBodies.Contains(body) };

            // A soft body is not one of the physics bodies the filter sees, so its hits come back with no body; the sweep
            // carries on a little past each of them.
            float travelled = 0f;
            for (int attempt = 0; attempt < 16 && travelled < distance; attempt++)
            {
                Vector3 start = from + (direction * travelled);
                float left = distance - travelled;

                if (!this.physicsManager.SphereCast(this.CameraRadius, start, direction, left, notTheTruck, out ShapeCastHit hit))
                {
                    return camera;
                }

                if (hit.Body != null)
                {
                    return start + (direction * (hit.Fraction * left));
                }

                travelled += (hit.Fraction * left) + this.CameraRadius;
            }

            return camera;
        }

        private bool FindCameras()
        {
            // The scene camera; an overlay camera such as the controls HUD draws only tagged entities.
            this.camera = this.Managers?.RenderManager?.ActiveCamera3D;
            if (!string.IsNullOrEmpty(this.camera?.TagFilter))
            {
                this.camera = this.Managers.EntityManager.FindComponentsOfType<Camera3D>().FirstOrDefault(c => string.IsNullOrEmpty(c.TagFilter));
            }

            if (this.camera?.Owner == null)
            {
                this.camera = null;
                return false;
            }

            this.freeCamera = this.camera.Owner.FindComponent<FreeCamera3D>();
            this.chaseFieldOfView = this.camera.FieldOfView;

            Entity driver = this.chassis.Owner.FindChild("DriverCamera");
            this.driverAnchor = driver?.FindComponent<Transform3D>();
            this.driverFieldOfView = driver?.FindComponent<Camera3D>(isExactType: false)?.FieldOfView ?? this.chaseFieldOfView;

            Entity fork = this.carriageSlider.Owner.FindChild("ForkCamera");
            this.forkAnchor = fork?.FindComponent<Transform3D>();
            this.forkFieldOfView = fork?.FindComponent<Camera3D>(isExactType: false)?.FieldOfView ?? this.chaseFieldOfView;

            for (int i = 0; i < this.orbits.Length; i++)
            {
                this.orbits[i].Zoom = this.orbits[i].TargetZoom = 1f;
            }

            return true;
        }

        /// <summary>
        /// The right mouse button turns the view, the wheel zooms and the middle button resets it; the view eases to
        /// what the mouse asks. Released, the driver's head comes back to the front.
        /// </summary>
        private void TurnWithMouse(ref Orbit orbit, float dt)
        {
            MouseDispatcher mouse = this.camera.Display?.MouseDispatcher;
            bool driver = this.view == CameraView.Driver;

            // Touch: a drag turns the view like the right mouse button, a pinch zooms, a double tap resets it.
            orbit.TargetYaw += this.touchYaw;
            orbit.TargetPitch += driver ? -this.touchPitch : this.touchPitch;
            orbit.TargetZoom = Math.Clamp(orbit.TargetZoom * this.touchZoom, 0.4f, 2.5f);
            if (this.touchReset)
            {
                orbit.TargetYaw = orbit.TargetPitch = 0f;
                orbit.TargetZoom = 1f;
            }

            this.touchYaw = this.touchPitch = 0f;
            this.touchZoom = 1f;
            this.touchReset = false;

            if (mouse?.IsButtonDown(MouseButtons.Right) != true && driver && !this.touchLookHeld)
            {
                // Released, the driver's head comes back to the front.
                orbit.TargetYaw = 0f;
                orbit.TargetPitch = 0f;
            }

            if (mouse != null)
            {
                if (mouse.IsButtonDown(MouseButtons.Right))
                {
                    orbit.TargetYaw -= mouse.PositionDelta.X * 0.005f;
                    orbit.TargetPitch += mouse.PositionDelta.Y * (driver ? -0.005f : 0.005f);
                }

                if (mouse.ScrollDelta.Y != 0)
                {
                    orbit.TargetZoom = Math.Clamp(orbit.TargetZoom * (mouse.ScrollDelta.Y > 0 ? 0.9f : 1.1f), 0.4f, 2.5f);
                }

                if (mouse.IsButtonDown(MouseButtons.Middle))
                {
                    orbit.TargetYaw = orbit.TargetPitch = 0f;
                    orbit.TargetZoom = 1f;
                }
            }

            if (driver)
            {
                orbit.TargetYaw = Math.Clamp(orbit.TargetYaw, -MathHelper.ToRadians(160f), MathHelper.ToRadians(160f));
                orbit.TargetPitch = Math.Clamp(orbit.TargetPitch, -MathHelper.ToRadians(50f), MathHelper.ToRadians(35f));
                orbit.TargetZoom = Math.Clamp(orbit.TargetZoom, 0.5f, 1.2f);
            }

            float ease = 1f - (float)Math.Exp(-dt * 10f);
            orbit.Yaw = MathHelper.Lerp(orbit.Yaw, orbit.TargetYaw, ease);
            orbit.Pitch = MathHelper.Lerp(orbit.Pitch, orbit.TargetPitch, ease);
            orbit.Zoom = MathHelper.Lerp(orbit.Zoom, orbit.TargetZoom, ease);
        }

        /// <summary>
        /// Turns an offset from the point a camera looks at about the vertical and up or down, and scales it by the zoom.
        /// </summary>
        private static Vector3 OrbitOffset(Vector3 offset, Orbit orbit, float minElevationDegrees)
        {
            float distance = offset.Length() * orbit.Zoom;
            float heading = (float)Math.Atan2(offset.X, offset.Z) + orbit.Yaw;
            float elevation = (float)Math.Atan2(offset.Y, Math.Sqrt((offset.X * offset.X) + (offset.Z * offset.Z))) + orbit.Pitch;
            elevation = Math.Clamp(elevation, MathHelper.ToRadians(minElevationDegrees), MathHelper.ToRadians(80f));

            return new Vector3(
                (float)(Math.Sin(heading) * Math.Cos(elevation)),
                (float)Math.Sin(elevation),
                (float)(Math.Cos(heading) * Math.Cos(elevation))) * distance;
        }

        private static Quaternion LookRotation(Vector3 position, Vector3 target)
        {
            Vector3 back = position - target;
            Vector3 up = Vector3.Up;
            Quaternion.CreateFromLookAt(ref back, ref up, out Quaternion orientation);
            return orientation;
        }
    }
}
