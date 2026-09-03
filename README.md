# VirtualShadowMaps

Clone

~~~bash
cd [Project]
git clone https://github.com/aespawang/VirtualShadowMap.git
~~~

Edit `[Project]/Packages/manifest.json`

~~~js
{
  "dependencies": {
    "com.shadow.vsm": "file:../VirtualShadowMaps/VSM",
    "com.unity.render-pipelines.universal": "file:../VirtualShadowMaps/com.unity.render-pipelines.universal",
    // ...
  }
}
~~~

Bistro

获取Bistro资产：https://developer.nvidia.com/orca/amazon-lumberyard-bistro
解压压缩包为一个目录，直接将目录拖入Unity即可导入