using Evergine.Common.Input.Keyboard;
using Evergine.Common.Input;
using Evergine.Framework;
using Evergine.Framework.Graphics;
using Evergine.Mathematics;
using System;
using System.Linq;
using Evergine.Framework.Physics3D;

namespace VehiclePhysicsSample.Vehicle
{
    public class CarController : Behavior
    {
        [BindComponent]
        private PhysicVehicle3D vehicle = null;

        [BindComponent(source: BindComponentSource.ChildrenSkipOwner, tag: "Steering", isRequired: false)]
        private Transform3D steeringWheel = null;

        private Camera3D[] AllCameras;

        private int selectedCameraIndex;

        public float MaxForce = 1000;
        public float Brake = 20;

        private float currentSteering = 0;
        private float steeringVelocity = 0;

        protected override void Start()
        {
            base.Start();

            this.AllCameras = this.Managers.EntityManager.FindComponentsOfType<Camera3D>().OrderBy(c => c.CameraOrder).ToArray();

            if (!Application.Current.IsEditor)
            {
                this.SelectCamera(); 
            }
        }

        protected override void Update(TimeSpan gameTime)
        {
            var keyboard = this.Managers.RenderManager.ActiveCamera3D.Display.KeyboardDispatcher;

            if (keyboard.ReadKeyState(Keys.C) == ButtonState.Pressing)
            {
                this.selectedCameraIndex = (this.selectedCameraIndex + 1) % this.AllCameras.Length;
                this.SelectCamera();
            }

            float engineForce = 0;

            if ((keyboard.ReadKeyState(Keys.W) == ButtonState.Pressed) || (keyboard.ReadKeyState(Keys.Up) == ButtonState.Pressed))
            {
                engineForce = MaxForce;
            }
            else if ((keyboard.ReadKeyState(Keys.S) == ButtonState.Pressed) || (keyboard.ReadKeyState(Keys.Down) == ButtonState.Pressed))
            {
                engineForce = -MaxForce / 3;
            }

            var brake = keyboard.ReadKeyState(Keys.Space) == ButtonState.Pressed ? Brake : 0;

            var rot = MathHelper.ToRadians(35);

            var steeringRotation = 0f;

            if ((keyboard.ReadKeyState(Keys.D) == ButtonState.Pressed) || (keyboard.ReadKeyState(Keys.Right) == ButtonState.Pressed))
            {
                steeringRotation = rot;

            }
            else if ((keyboard.ReadKeyState(Keys.A) == ButtonState.Pressed) || (keyboard.ReadKeyState(Keys.Left) == ButtonState.Pressed))
            {
                steeringRotation = -rot;
            }

            this.currentSteering = MathHelper.SmoothDamp(this.currentSteering, steeringRotation, ref this.steeringVelocity, 0.5f, (float)gameTime.TotalSeconds);

            ////engineForce = MaxForce;
            this.vehicle.ApplyEngineForce(engineForce);
            this.vehicle.SetSteeringValue(this.currentSteering);
            this.vehicle.SetBrake(brake);

            if (this.steeringWheel != null)
            {
                var rotation = this.steeringWheel.LocalRotation;
                rotation.Z = this.currentSteering * 5;
                this.steeringWheel.LocalRotation= rotation;
            }
        }

        private void SelectCamera()
        {
            for (int i = 0; i < this.AllCameras.Length; i++)
            {
                var enabled = i == this.selectedCameraIndex;
                this.AllCameras[i].Owner.IsEnabled = enabled;
            }
        }
    }
}
