using UnityEngine;

namespace vsm
{
    internal static class Utilities
    {
        public static Matrix4x4 BuildLightMatrix(Camera lightCamera)
        {
            if (!lightCamera) return Matrix4x4.identity;
            var projection = FixProjection(lightCamera.projectionMatrix);
            return projection * lightCamera.worldToCameraMatrix;
        }

        private static Matrix4x4 FixProjection(Matrix4x4 projectionMatrix)
        {
            var proj = projectionMatrix;
            proj[2, 0] = (proj[2, 0] * -0.5f) + (proj[3, 0] * 0.5f);
            proj[2, 1] = (proj[2, 1] * -0.5f) + (proj[3, 1] * 0.5f);
            proj[2, 2] = (proj[2, 2] * -0.5f) + (proj[3, 2] * 0.5f);
            proj[2, 3] = (proj[2, 3] * -0.5f) + (proj[3, 3] * 0.5f);
            return proj;
        }
    }
}