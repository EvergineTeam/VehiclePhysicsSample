using Evergine.Framework;
using Evergine.Framework.Graphics;
using Evergine.Mathematics;
using System;

namespace VehiclePhysicsSample.Camera
{
    public class TargetComponent : Behavior
    {
        [BindComponent]
        private Transform3D transform = null;

        private Transform3D trackedTransform;

        public string TrackedEntityPath { get; set; }

        private Vector3 positionVelocity;

        public float Speed;

        protected override void OnLoaded()
        {
            base.OnLoaded();
            this.UpdateOrder = 1;
        }

        protected override void Start()
        {
            base.Start();
            if (!string.IsNullOrEmpty(this.TrackedEntityPath))
            {
                this.trackedTransform = this.Managers.EntityManager.FindComponentFromEntityPath<Transform3D>(this.TrackedEntityPath);
            }
        }

        protected override void Update(TimeSpan gameTime)
        {
            if(this.trackedTransform != null)
            {
                this.transform.Position = Vector3.SmoothDamp(this.transform.Position, this.trackedTransform.Position, ref this.positionVelocity, this.Speed, (float)gameTime.TotalSeconds);
                this.transform.Orientation = this.trackedTransform.Orientation;
            }
        }
    }
}
