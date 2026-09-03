using System;
using Unity.Mathematics;
using UnityEngine;

namespace vsm
{
    [Serializable]
    public class VSMConfig
    {
        public int pageResolution = 128;
        public int2 virtualTextureGridSize = new(8, 8);
        public int2 physicalTextureGridSize = new(8, 4);
        
        public int2 VirtualTextureResolution => virtualTextureGridSize * pageResolution;
        public int2 PhysicalTextureResolution => physicalTextureGridSize * pageResolution;

        public bool enablePageCache = true;
        public bool enablePhyPageStatusDebugBuffer;
        public float distanceSensitivity = 0.1f;

        public int GetMipCount()
        {
            var maxSize = Mathf.Max(virtualTextureGridSize.x, virtualTextureGridSize.y);
            var mipCount = Mathf.FloorToInt(Mathf.Log(maxSize, 2)) + 1;
            return mipCount;
        }
    }
}