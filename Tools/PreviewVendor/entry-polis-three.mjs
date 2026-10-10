// 游戏模式页面（Assets/Polis，由 OfficePreviewHost 的 /polis/ 路由提供）用到的 three.js 与几个附加模块，打成一个自包含的 ESM 文件，
// 网页离线可用、不引用 CDN。附加模块内部 import 'three'，打在一起才不需要 importmap。
export * from 'three';
export { OrbitControls } from 'three/addons/controls/OrbitControls.js';
export { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
export { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
export { GTAOPass } from 'three/addons/postprocessing/GTAOPass.js';
export { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
export { ShaderPass } from 'three/addons/postprocessing/ShaderPass.js';
export { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
export { mergeGeometries, mergeVertices } from 'three/addons/utils/BufferGeometryUtils.js';
