using System;
using System.Collections.Generic;
using System.Linq;
using Evergine.Common.Graphics;
using Evergine.Common.Input;
using Evergine.Common.Input.Keyboard;
using Evergine.Common.Input.Mouse;
using Evergine.Common.Input.Pointer;
using Evergine.Components.Fonts;
using Evergine.Framework;
using Evergine.Framework.Graphics;
using Evergine.Mathematics;

namespace PhysicSample.Components
{
    /// <summary>
    /// On-screen controls for phones and tablets, drawn by the HUD camera. The pieces are authored in the scene: the
    /// DriveStick and MastStick (each a Base and a Knob), hidden until used, and buttons named Button_[action] (each a
    /// Cap and a Label) under corner anchors (TopRight, TopLeft). What the fingers do:
    /// <list type="bullet">
    /// <item>A thumb on the lower left of the screen brings up the drive stick right there: up and down drive forward
    /// and back, left and right steer.</item>
    /// <item>A thumb on the lower right brings up the mast stick: up and down lift and lower the forks, left and right
    /// close and open them.</item>
    /// <item>Higher up, one finger turns the view, two fingers pinch to zoom and a double tap resets the view.</item>
    /// <item>The VIEW button goes to the next camera.</item>
    /// </list>
    /// The controls take over when a finger touches the screen and give way to the keyboard help when a key is pressed.
    /// </summary>
    public class TouchControls : Behavior
    {
        private const long NoPointer = long.MinValue;
        private static readonly Color Idle = new Color(230, 233, 238, 255);
        private static readonly Color Pressed = new Color(255, 196, 64, 255);
        private static readonly Keys[] KeyboardKeys =
        {
            Keys.W, Keys.A, Keys.S, Keys.D, Keys.Q, Keys.E, Keys.R, Keys.F, Keys.X, Keys.Z, Keys.C, Keys.H, Keys.Space,
            Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.PageUp, Keys.PageDown, Keys.Home, Keys.End,
        };

        private Camera3D hudCamera;
        private ControlsHud hud;
        private ForkliftVehicle forklift;
        private Transform3D topRight;
        private Transform3D topLeft;
        private Stick drive;
        private Stick mast;
        private readonly List<Button> buttons = new List<Button>();
        private Text3DMesh viewName;
        private bool found;
        private bool touchMode;
        private float halfWidth;
        private float halfHeight;

        private readonly Dictionary<long, Vector2> points = new Dictionary<long, Vector2>();
        private readonly Dictionary<long, Vector2> lastPoints = new Dictionary<long, Vector2>();
        private readonly HashSet<long> lookPointers = new HashSet<long>();
        private float lastTapTime = -10f;
        private Vector2 lastTapPosition;
        private float time;

        /// <summary>Gets or sets the gap between the controls and the edges of the screen, in HUD units (the screen is OrthographicSize high).</summary>
        public float Margin { get; set; } = 0.6f;

        /// <summary>Gets or sets how far a stick's knob travels from its centre, in HUD units, for full input.</summary>
        public float StickRange { get; set; } = 3.2f;

        /// <summary>Gets or sets the share of the screen height, from the bottom, where a thumb brings up a stick; above it fingers turn the view.</summary>
        public float StickZoneHeight { get; set; } = 0.3f;

        /// <summary>Gets or sets the share of the stick travel near the centre that does nothing.</summary>
        public float DeadZone { get; set; } = 0.12f;

        /// <summary>Gets or sets how much a drag turns the view, in radians per HUD unit.</summary>
        public float LookSpeed { get; set; } = 0.12f;

        /// <summary>Gets or sets a value indicating whether the left mouse button acts as a finger, to try the controls on a PC.</summary>
        public bool MouseActsAsTouch { get; set; } = false;

        private enum Action
        {
            View,
        }

        private class Stick
        {
            public Entity Entity;
            public Transform3D Root;
            public Transform3D Knob;
            public Text3DMesh Label;
            public long Pointer = NoPointer;
            public Vector2 Center;
            public Vector2 Value;
        }

        private class Button
        {
            public Action Action;
            public Transform3D Root;
            public Transform3D Cap;
            public Text3DMesh Label;
            public float Radius;
            public long Pointer = NoPointer;

            public bool IsDown => this.Pointer != NoPointer;
        }

        /// <inheritdoc/>
        protected override void Update(TimeSpan gameTime)
        {
            this.time += (float)gameTime.TotalSeconds;

            if (!this.found && !this.FindParts())
            {
                return;
            }

            this.halfHeight = this.hudCamera.OrthographicSize * 0.5f;
            this.halfWidth = this.halfHeight * this.hudCamera.AspectRatio;
            this.PlaceAnchors();

            Display display = this.hudCamera.Display;
            if (display == null || display.Width == 0 || display.Height == 0)
            {
                return;
            }

            this.ReadPoints(display);

            if (this.points.Count > 0 && !this.touchMode)
            {
                this.SetTouchMode(true);
            }
            else if (this.touchMode && this.points.Count == 0 && this.KeyboardOrMouseUsed(display))
            {
                this.SetTouchMode(false);
            }

            if (!this.touchMode)
            {
                return;
            }

            this.AssignNewPointers();
            this.ReleaseLiftedPointers();
            this.UpdateStick(this.drive);
            this.UpdateStick(this.mast);
            this.UpdateLook();

            this.forklift?.SetTouchInput(new ForkliftInput
            {
                Throttle = this.drive?.Value.Y ?? 0f,
                Steer = this.drive?.Value.X ?? 0f,
                Lift = this.mast?.Value.Y ?? 0f,
                Spread = this.mast?.Value.X ?? 0f,
            });

            foreach (var button in this.buttons)
            {
                Highlight(button.Cap, button.Label, button.IsDown);
            }

            if (this.viewName != null && this.forklift != null)
            {
                this.viewName.Text = this.forklift.CameraViewName;
            }

            foreach (var point in this.points)
            {
                this.lastPoints[point.Key] = point.Value;
            }
        }

        /// <summary>
        /// Finds the parts on the first update: when this component attaches, the child entities may not be attached yet.
        /// </summary>
        private bool FindParts()
        {
            this.hudCamera = this.Owner.Parent?.FindComponent<Camera3D>();
            this.hud = this.Owner.Parent?.FindComponentInChildren<ControlsHud>();
            this.forklift = this.Managers.EntityManager.FindComponentsOfType<ForkliftVehicle>().FirstOrDefault();
            if (this.hudCamera == null)
            {
                return false;
            }

            this.topRight = this.Owner.FindChild("TopRight")?.FindComponent<Transform3D>();
            this.topLeft = this.Owner.FindChild("TopLeft")?.FindComponent<Transform3D>();

            var all = this.Owner.FindChildrenByTag("TOUCH", isRecursive: true).ToList();
            this.drive = MakeStick(all.FirstOrDefault(e => e.Name == "DriveStick"));
            this.mast = MakeStick(all.FirstOrDefault(e => e.Name == "MastStick"));
            this.viewName = all.FirstOrDefault(e => e.Name == "ViewName")?.FindComponent<Text3DMesh>();

            foreach (Entity entity in all.Where(e => e.Name.StartsWith("Button_", StringComparison.Ordinal)))
            {
                if (Enum.TryParse(entity.Name.Substring(7), out Action action))
                {
                    Entity cap = entity.FindChild("Cap");
                    this.buttons.Add(new Button
                    {
                        Action = action,
                        Root = entity.FindComponent<Transform3D>(),
                        Cap = cap?.FindComponent<Transform3D>(),
                        Label = entity.FindChild("Label")?.FindComponent<Text3DMesh>(),
                        Radius = (cap?.FindComponent<Evergine.Components.Graphics3D.CylinderMesh>()?.Diameter ?? 2.8f) * 0.5f,
                    });
                }
            }

            // The sticks only show while a thumb holds them.
            Show(this.drive, false);
            Show(this.mast, false);

            this.found = true;
            return true;
        }

        private static Stick MakeStick(Entity entity)
        {
            return entity == null ? null : new Stick
            {
                Entity = entity,
                Root = entity.FindComponent<Transform3D>(),
                Knob = entity.FindChild("Knob")?.FindComponent<Transform3D>(),
                Label = entity.FindChild("Label")?.FindComponent<Text3DMesh>(),
            };
        }

        private static void Show(Stick stick, bool visible)
        {
            if (stick?.Entity != null && stick.Entity.IsEnabled != visible)
            {
                stick.Entity.IsEnabled = visible;
            }
        }

        /// <summary>Keeps each anchor in its corner of the orthographic view, whatever the shape of the screen.</summary>
        private void PlaceAnchors()
        {
            float x = this.halfWidth - this.Margin;
            float y = this.halfHeight - this.Margin;

            if (this.topRight != null) this.topRight.LocalPosition = new Vector3(x, y, 0f);
            if (this.topLeft != null) this.topLeft.LocalPosition = new Vector3(-x, y, 0f);
        }

        /// <summary>The fingers on the screen (and the left mouse button when it acts as one), in HUD units from the centre.</summary>
        private void ReadPoints(Display display)
        {
            this.points.Clear();

            Vector2 ToHud(float px, float py) => new Vector2(
                ((px / display.Width) - 0.5f) * 2f * this.halfWidth,
                (0.5f - (py / display.Height)) * 2f * this.halfHeight);

            PointerDispatcher touch = display.TouchDispatcher;
            if (touch != null)
            {
                foreach (PointerPoint point in touch.Points)
                {
                    if (point.State == ButtonState.Pressing || point.State == ButtonState.Pressed)
                    {
                        this.points[point.Id] = ToHud(point.Position.X, point.Position.Y);
                    }
                }
            }

            MouseDispatcher mouse = display.MouseDispatcher;
            if (this.MouseActsAsTouch && mouse != null && mouse.IsButtonDown(MouseButtons.Left))
            {
                this.points[-1] = ToHud(mouse.Position.X, mouse.Position.Y);
            }
        }

        private bool KeyboardOrMouseUsed(Display display)
        {
            KeyboardDispatcher keyboard = display.KeyboardDispatcher;
            MouseDispatcher mouse = display.MouseDispatcher;
            return (keyboard != null && KeyboardKeys.Any(keyboard.IsKeyDown))
                || (mouse != null && (mouse.IsButtonDown(MouseButtons.Right) || mouse.IsButtonDown(MouseButtons.Middle) || mouse.ScrollDelta.Y != 0));
        }

        private void SetTouchMode(bool on)
        {
            this.touchMode = on;
            this.hud?.SetTouchMode(on);

            if (!on)
            {
                // Hand everything back to the keyboard and mouse.
                foreach (var button in this.buttons)
                {
                    button.Pointer = NoPointer;
                }

                foreach (var stick in new[] { this.drive, this.mast })
                {
                    if (stick != null)
                    {
                        stick.Pointer = NoPointer;
                        stick.Value = Vector2.Zero;
                        Show(stick, false);
                    }
                }

                this.lookPointers.Clear();
                this.lastPoints.Clear();
                this.forklift?.SetTouchInput(default);
                this.forklift?.TouchLook(false, 0f, 0f);
            }
        }

        /// <summary>
        /// A finger that has just touched gets the button it lands on; on the lower part of the screen it brings up the
        /// stick of its side, centred where it landed; higher up it turns the view.
        /// </summary>
        private void AssignNewPointers()
        {
            foreach (var point in this.points)
            {
                long id = point.Key;
                Vector2 at = point.Value;
                if (this.lastPoints.ContainsKey(id))
                {
                    continue;
                }

                this.lastPoints[id] = at;

                Button button = this.buttons.FirstOrDefault(b => b.Pointer == NoPointer && Vector2.Distance(at, this.HudPosition(b.Root)) <= b.Radius * 1.25f);
                if (button != null)
                {
                    button.Pointer = id;
                    if (button.Action == Action.View)
                    {
                        this.forklift?.NextView(skipFree: true);
                    }

                    continue;
                }

                bool lower = at.Y < -this.halfHeight + (2f * this.halfHeight * this.StickZoneHeight);
                if (lower)
                {
                    Stick stick = at.X < 0f ? this.drive : this.mast;
                    if (stick != null && stick.Pointer == NoPointer)
                    {
                        // Centred under the thumb, pulled in from the edges so the whole stick stays on screen.
                        float reach = this.StickRange + this.Margin;
                        stick.Center = new Vector2(
                            Math.Clamp(at.X, -this.halfWidth + reach, this.halfWidth - reach),
                            Math.Clamp(at.Y, -this.halfHeight + reach, this.halfHeight - reach));
                        stick.Pointer = id;
                        stick.Value = Vector2.Zero;
                        Show(stick, true);
                    }

                    // A second thumb on a side whose stick is already held does nothing.
                    continue;
                }

                this.lookPointers.Add(id);

                // A second tap close to the first one, quickly, puts the view back.
                if (this.time - this.lastTapTime < 0.3f && Vector2.Distance(at, this.lastTapPosition) < 2f)
                {
                    this.forklift?.ResetView();
                    this.lastTapTime = -10f;
                }
                else
                {
                    this.lastTapTime = this.time;
                    this.lastTapPosition = at;
                }
            }
        }

        private void ReleaseLiftedPointers()
        {
            foreach (long id in this.lastPoints.Keys.Where(id => !this.points.ContainsKey(id)).ToList())
            {
                this.lastPoints.Remove(id);
                this.lookPointers.Remove(id);

                foreach (var button in this.buttons.Where(b => b.Pointer == id))
                {
                    button.Pointer = NoPointer;
                }

                foreach (var stick in new[] { this.drive, this.mast })
                {
                    if (stick != null && stick.Pointer == id)
                    {
                        stick.Pointer = NoPointer;
                        stick.Value = Vector2.Zero;
                        Show(stick, false);
                    }
                }
            }
        }

        /// <summary>The knob follows the thumb within the stick's range; the input is how far it is, past the dead zone.</summary>
        private void UpdateStick(Stick stick)
        {
            if (stick == null || stick.Pointer == NoPointer || !this.points.TryGetValue(stick.Pointer, out Vector2 at))
            {
                return;
            }

            Vector2 offset = at - stick.Center;
            if (offset.Length() > this.StickRange)
            {
                offset = Vector2.Normalize(offset) * this.StickRange;
            }

            Vector2 value = offset / this.StickRange;
            stick.Value = new Vector2(ApplyDeadZone(value.X, this.DeadZone), ApplyDeadZone(value.Y, this.DeadZone));

            stick.Root.LocalPosition = new Vector3(stick.Center.X, stick.Center.Y, 0f);
            if (stick.Knob != null)
            {
                stick.Knob.LocalPosition = new Vector3(offset.X, offset.Y, stick.Knob.LocalPosition.Z);
            }

            Highlight(stick.Knob, stick.Label, true);
        }

        /// <summary>One finger turns the view; two pinch to zoom.</summary>
        private void UpdateLook()
        {
            if (this.forklift == null)
            {
                return;
            }

            var fingers = this.lookPointers.Where(this.points.ContainsKey).Take(2).ToList();
            if (fingers.Count == 1)
            {
                long id = fingers[0];
                Vector2 delta = this.lastPoints.TryGetValue(id, out Vector2 last) ? this.points[id] - last : Vector2.Zero;
                this.forklift.TouchLook(true, -delta.X * this.LookSpeed, -delta.Y * this.LookSpeed);
            }
            else if (fingers.Count == 2)
            {
                Vector2 a = this.points[fingers[0]], b = this.points[fingers[1]];
                if (this.lastPoints.TryGetValue(fingers[0], out Vector2 lastA) && this.lastPoints.TryGetValue(fingers[1], out Vector2 lastB))
                {
                    float before = Vector2.Distance(lastA, lastB);
                    float now = Vector2.Distance(a, b);
                    if (before > 0.5f && now > 0.5f)
                    {
                        this.forklift.TouchZoom(before / now);
                    }
                }

                this.forklift.TouchLook(true, 0f, 0f);
            }
            else
            {
                this.forklift.TouchLook(false, 0f, 0f);
            }
        }

        /// <summary>Where an entity is on the HUD, in HUD units from the centre of the screen.</summary>
        private Vector2 HudPosition(Transform3D entity)
        {
            Vector3 local = entity.Position - this.hudCamera.Transform.Position;
            return new Vector2(local.X, local.Y);
        }

        private static float ApplyDeadZone(float value, float deadZone)
        {
            float magnitude = Math.Abs(value);
            return magnitude <= deadZone ? 0f : Math.Sign(value) * Math.Min(1f, (magnitude - deadZone) / (1f - deadZone));
        }

        private static void Highlight(Transform3D cap, Text3DMesh label, bool down)
        {
            if (cap != null)
            {
                cap.LocalScale = down ? new Vector3(1.12f) : Vector3.One;
            }

            if (label != null)
            {
                label.Color = down ? Pressed : Idle;
            }
        }
    }
}
