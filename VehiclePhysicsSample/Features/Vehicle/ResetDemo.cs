using Evergine.Common.Input;
using Evergine.Common.Input.Keyboard;
using Evergine.Framework;
using Evergine.Framework.Graphics;
using Evergine.Framework.Physics;
using Evergine.Mathematics;
using System;
using System.Linq;

namespace VehiclePhysicsSample.Features.Vehicle
{
    public class ResetDemo : Behavior
    {
        private RigidBody[] bodies;
        private Matrix4x4[] transforms;

        [BindComponent]
        private Transform3D transform = null;

        public float SpawnThreshold = -10;

        protected override void Start()
        {
            base.Start();

            this.bodies = this.Managers.EntityManager.FindComponentsOfType<RigidBody>().Where(b => !b.IsKinematic).ToArray();
            this.transforms = this.bodies.Select(b => b.Transform3D.WorldTransform).ToArray();
        }

        protected override void Update(TimeSpan gameTime)
        {
            var keyboard = this.Managers.RenderManager.ActiveCamera3D.Display.KeyboardDispatcher;

            if (keyboard.ReadKeyState(Keys.Tab) == ButtonState.Pressing || (keyboard.ReadKeyState(Keys.Enter) == ButtonState.Pressing))
            {
                this.SpawnDemo();
            }

            if (this.transform.Position.Y < this.SpawnThreshold)
            {
                this.SpawnDemo();
            }
        }

        private void SpawnDemo()
        {
            for (int i = 0; i < this.bodies.Length; i++)
            {
                var b = this.bodies[i];
                var t = this.transforms[i];
                b.Teleport(t.Translation, t.Orientation);
                b.LinearVelocity = default;
                b.AngularVelocity = default;
            }
        }
    }
}
