using Evergine.Framework;
using Evergine.Framework.Graphics;
using System;

namespace VehiclePhysicsSample.Features.Elevator
{
    public class Elevator : Behavior
    {
        [BindComponent]
        private Transform3D transform = null;

        public float Amplitude = 2;

        private float initY;
        private double accumTime;

        protected override void Start()
        {
            base.Start();
            initY = transform.LocalPosition.Y;
            accumTime = 0;
        }


        protected override void Update(TimeSpan gameTime)
        {
            var pos = transform.LocalPosition;
            pos.Y = initY + (float)(Math.Sin((float)accumTime) * 0.5f + 0.5f) * Amplitude;
            transform.LocalPosition = pos;

            accumTime += gameTime.TotalSeconds;
        }
    }
}
