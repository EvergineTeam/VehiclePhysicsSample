using Evergine.Framework;
using Evergine.Framework.Graphics;
using Evergine.Mathematics;
using System;
using System.Collections.Generic;
using System.Text;

namespace VehiclePhysicsSample.Camera
{
    public class FollowComponent : Behavior
    {
        [BindComponent]
        private Transform3D transform = null;

        private Transform3D trackedTransform;

        public string TrackedEntityPath { get; set; }

        private Vector3 deltaPosition;
        private Vector3 positionVelocity;

        private float distanceFactor = 0.7f;

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

                this.deltaPosition = (this.transform.Position - this.trackedTransform.Position) * this.distanceFactor;
            }
        }

        protected override void Update(TimeSpan gameTime)
        {
            if(this.trackedTransform != null)
            {
                this.transform.Position = Vector3.SmoothDamp(this.transform.Position, this.trackedTransform.Position + this.deltaPosition, ref this.positionVelocity, 0.0f, (float)gameTime.TotalSeconds);
            }
        }
    }
}
