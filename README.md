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