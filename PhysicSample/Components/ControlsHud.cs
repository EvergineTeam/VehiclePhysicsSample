using System;
using System.Collections.Generic;
using System.Linq;
using Evergine.Common.Graphics;
using Evergine.Common.Input.Keyboard;
using Evergine.Common.Input.Mouse;
using Evergine.Components.Fonts;
using Evergine.Framework;
using Evergine.Framework.Graphics;
using Evergine.Mathematics;

namespace PhysicSample.Components
{
    /// <summary>
    /// The controls help in a corner of the screen. The panel is authored in the scene under the orthographic HUD camera:
    /// every key is an entity named Key_[key] with a Cap plane and a Label text. This keeps it in the bottom-left corner
    /// whatever the window size, lights up the keys while they are pressed, shows the current camera view and folds the
    /// panel away with H.
    /// </summary>
    public class ControlsHud : Behavior
    {
        private static readonly Color Idle = new Color(230, 233, 238, 255);
        private static readonly Color Pressed = new Color(255, 196, 64, 255);

        [BindComponent]
        private Transform3D transform = null;

        private Camera3D hudCamera;
        private Entity panel;
        private Entity collapsed;
        private Text3DMesh viewText;
        private Text3DMesh modeHint;
        private ForkliftVehicle forklift;
        private readonly List<(string Key, Transform3D Cap, Text3DMesh Label)> keys = new List<(string, Transform3D, Text3DMesh)>();
        private bool keyWasDown;
        private string shownView;
        private bool expanded;
        private bool touchMode;

        /// <summary>Gets or sets the gap between the panel and the edges of the screen, in HUD units (the screen is 20 high).</summary>
        public float Margin { get; set; } = 0.35f;

        /// <summary>Gets or sets a value indicating whether the full panel shows when the scene starts.</summary>
        public bool ShowAtStart { get; set; } = true;

        /// <inheritdoc/>
        protected override bool OnAttached()
        {
            if (!base.OnAttached())
            {
                return false;
            }

            this.hudCamera = this.Owner.Parent?.FindComponent<Camera3D>();
            this.BlendOverScene();
            return true;
        }

        /// <summary>
        /// The HUD camera renders to its own buffer, cleared to transparent every frame, and that buffer is blended over
        /// the scene. Evergine only blends a camera that does not clear its target, which would leave the old frames of
        /// the panel behind, so the blending is forced here.
        /// </summary>
        private void BlendOverScene()
        {
            if (this.hudCamera?.DrawContext != null)
            {
                this.hudCamera.DrawContext.ForceBlitAlphaBlending = true;
            }
        }

        /// <summary>
        /// Finds the parts of the panel. Done on the first update: when this component attaches, the child entities of
        /// the panel may not be attached yet.
        /// </summary>
        private bool FindParts()
        {
            this.panel = this.Owner.FindChild("Panel");
            this.collapsed = this.Owner.FindChild("Collapsed");
            if (this.panel == null)
            {
                return false;
            }

            this.viewText = this.panel.FindChild("View")?.FindComponent<Text3DMesh>();
            this.modeHint = this.panel.FindChild("ModeHint")?.FindComponent<Text3DMesh>();

            foreach (Entity key in this.Owner.FindChildrenByTag("HUD", isRecursive: true).Concat(this.Owner.FindChildrenByTag("HUDMIN", isRecursive: true)).Where(e => e.Name.StartsWith("Key_", StringComparison.Ordinal)))
            {
                this.keys.Add((key.Name.Substring(4), key.FindChild("Cap")?.FindComponent<Transform3D>(), key.FindChild("Label")?.FindComponent<Text3DMesh>()));
            }

            this.SetExpanded(this.ShowAtStart);
            return true;
        }

        /// <inheritdoc/>
        protected override void Update(TimeSpan gameTime)
        {
            if (this.hudCamera == null || (this.panel == null && !this.FindParts()))
            {
                return;
            }

            this.BlendOverScene();

            // Bottom-left corner of the orthographic view, which is OrthographicSize high.
            float halfHeight = this.hudCamera.OrthographicSize * 0.5f;
            float halfWidth = halfHeight * this.hudCamera.AspectRatio;
            this.transform.LocalPosition = new Vector3(-halfWidth + this.Margin, -halfHeight + this.Margin, -5f);

            var display = this.hudCamera.Display;
            KeyboardDispatcher keyboard = display?.KeyboardDispatcher;
            MouseDispatcher mouse = display?.MouseDispatcher;

            bool toggle = keyboard?.IsKeyDown(Keys.H) == true;

            if (toggle && !this.keyWasDown && !this.touchMode)
            {
                this.SetExpanded(!this.expanded);
            }

            this.keyWasDown = toggle;

            foreach (var (key, cap, label) in this.keys)
            {
                bool down = IsDown(key, keyboard, mouse);
                if (label != null)
                {
                    label.Color = down ? Pressed : Idle;
                }

                if (cap != null)
                {
                    cap.LocalScale = down ? new Vector3(1.12f) : Vector3.One;
                }
            }

            this.forklift ??= this.Managers.EntityManager.FindComponentsOfType<ForkliftVehicle>().FirstOrDefault();
            string view = this.forklift?.CameraViewName ?? "Chase";
            if (view != this.shownView)
            {
                this.shownView = view;
                if (this.viewText != null)
                {
                    this.viewText.Text = "View: " + view;
                }

                if (this.modeHint != null)
                {
                    this.modeHint.Text = view == "Free"
                        ? "Free camera: W A S D / Q E fly - drive with the arrow keys"
                        : "Drive with the letters or the arrow keys";
                }
            }
        }

        /// <summary>
        /// Shows the full panel or the folded one. The HUD camera draws only the entities with its tag: the panel is
        /// tagged HUD and the folded one HUDMIN, so the switch is the camera's tag filter.
        /// </summary>
        private void SetExpanded(bool expanded)
        {
            this.expanded = expanded;
            this.ApplyFilter();
        }

        /// <summary>
        /// Shows the on-screen touch controls (entities tagged TOUCH) instead of the keyboard help, or the help again.
        /// Called by <see cref="TouchControls"/> when a finger touches the screen or a key is pressed.
        /// </summary>
        /// <param name="touch">Whether the touch controls are in use.</param>
        public void SetTouchMode(bool touch)
        {
            this.touchMode = touch;
            this.ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (this.hudCamera != null)
            {
                this.hudCamera.TagFilter = this.touchMode ? "TOUCH" : this.expanded ? "HUD" : "HUDMIN";
            }
        }

        private static bool IsDown(string key, KeyboardDispatcher keyboard, MouseDispatcher mouse)
        {
            switch (key)
            {
                case "RMB": return mouse?.IsButtonDown(MouseButtons.Right) == true;
                case "MMB": return mouse?.IsButtonDown(MouseButtons.Middle) == true;
                case "Wheel": return mouse != null && mouse.ScrollDelta.Y != 0;
                case "W": return Any(keyboard, Keys.W, Keys.Up);
                case "S": return Any(keyboard, Keys.S, Keys.Down);
                case "A": return Any(keyboard, Keys.A, Keys.Left);
                case "D": return Any(keyboard, Keys.D, Keys.Right);
                case "E": return Any(keyboard, Keys.E, Keys.PageUp);
                case "Q": return Any(keyboard, Keys.Q, Keys.PageDown);
                case "R": return Any(keyboard, Keys.R, Keys.Home);
                case "F": return Any(keyboard, Keys.F, Keys.End);
                default: return keyboard != null && Enum.TryParse(key, out Keys k) && keyboard.IsKeyDown(k);
            }
        }

        private static bool Any(KeyboardDispatcher keyboard, Keys a, Keys b) => keyboard != null && (keyboard.IsKeyDown(a) || keyboard.IsKeyDown(b));
    }
}
