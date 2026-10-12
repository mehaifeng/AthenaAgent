// 渲染：正交相机 + 后期（环境光遮蔽 → 色调映射 → 调色 + 雅典娜的光 + 暗角 + 颗粒），取自打样。
// 世界是彩色的（2026-10-12，用户：世界上色）；黑白调色（只给标记物体留色）仍在，?grade=1 打开，用来对照明暗关系。
//
// 屏幕空间效果（光晕、旁白气泡、建筑名牌）必须用"这一帧真正用来渲染的"相机矩阵投影（设计稿 12.2）：
// three.js 的 lookAt（OrbitControls 每帧都调）会先用"新位置 + 旧朝向"刷新矩阵，直到 render 才改正。
// 所以每帧的顺序固定为：移动相机与人物 → syncMatrices() → 计算所有屏幕空间位置 → 渲染。
import * as THREE from '../vendor/three.bundle.mjs';
import { EffectComposer, RenderPass, GTAOPass, OutputPass, ShaderPass, RoomEnvironment } from '../vendor/three.bundle.mjs';

/**
 * 地图外的纯色（2026-10-12，用户：地图是有限的矩形，矩形外是跟着主题色变的纯色）。和宿主视图 PolisView 的底色是同一对值——
 * 页面还没画出来时看到的就是这个颜色，画出来之后边缘外也还是它。按显示空间（sRGB）给，调色时原样写出。
 */
export const FILL = Object.freeze({ day: 0xe4dfd6, night: 0x15181d });

export function createRenderer({ canvas, ao = true, shadows = true, grade = false, accent = true }) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setSize(window.innerWidth, window.innerHeight, false);
  renderer.shadowMap.enabled = shadows;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 0.82;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0xbcd2e2);
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  scene.environmentIntensity = 0.18;

  const hemi = new THREE.HemisphereLight(0xdfeaf5, 0x9a8466, 0.55);
  scene.add(hemi);
  const sun = new THREE.DirectionalLight(0xfff2df, 3.6);
  sun.castShadow = shadows;
  sun.shadow.mapSize.set(4096, 4096);
  const sc = sun.shadow.camera;
  sc.left = -48; sc.right = 48; sc.top = 48; sc.bottom = -48; sc.near = 1; sc.far = 300;
  sun.shadow.bias = -0.0003; sun.shadow.normalBias = 0.03; sun.shadow.radius = 3;
  scene.add(sun, sun.target);

  const aspect = () => window.innerWidth / window.innerHeight;
  const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 1200);
  camera.userData.frustum = 34;
  applyFrustum();

  function applyFrustum() {
    const f = camera.userData.frustum, a = aspect();
    camera.left = -f * a / 2; camera.right = f * a / 2; camera.top = f / 2; camera.bottom = -f / 2;
    camera.updateProjectionMatrix();
  }

  const size = new THREE.Vector2();
  renderer.getDrawingBufferSize(size);
  const composer = new EffectComposer(renderer);
  composer.addPass(new RenderPass(scene, camera));
  const gtao = new GTAOPass(scene, camera, size.x, size.y);
  gtao.output = GTAOPass.OUTPUT.Default;
  gtao.updateGtaoMaterial({ radius: 0.8, distanceExponent: 1.2, thickness: 1.2, scale: 1.0, samples: 16, distanceFallOff: 1.0, screenSpaceRadius: false });
  gtao.updatePdMaterial({ lumaPhi: 10, depthPhi: 2, normalPhi: 3, radius: 6, samples: 16 });
  gtao.blendIntensity = 1.0;
  if (ao) composer.addPass(gtao);
  composer.addPass(new OutputPass());

  const maskTarget = new THREE.WebGLRenderTarget(size.x, size.y);
  const MASK_ACCENT = new THREE.MeshBasicMaterial({ color: 0xff0000, fog: false });
  const MASK_ATHENA = new THREE.MeshBasicMaterial({ color: 0x00ff00, fog: false });
  const MASK_OFF = new THREE.MeshBasicMaterial({ color: 0x000000, fog: false });
  // 遮罩的背景是蓝（0, 0, 1）：画到的东西都没有蓝分量，蓝的地方就是地图外——调色时那里整片换成主题的纯色
  const maskBackground = new THREE.Color(0x0000ff);

  function renderMask() {
    const saved = new Map();
    scene.traverse((o) => {
      if (o.isMesh) { saved.set(o, o.material); o.material = o.userData.accent ? MASK_ACCENT : o.userData.athena ? MASK_ATHENA : MASK_OFF; }
    });
    const bg = scene.background, env = scene.environment;
    scene.background = maskBackground;
    renderer.shadowMap.autoUpdate = false;
    renderer.setRenderTarget(maskTarget);
    renderer.clear();
    renderer.render(scene, camera);
    renderer.setRenderTarget(null);
    renderer.shadowMap.autoUpdate = true;
    scene.background = bg;
    scene.environment = env;
    saved.forEach((m, o) => { o.material = m; });
  }

  const gradePass = new ShaderPass({
    uniforms: {
      tDiffuse: { value: null },
      tMask: { value: null },
      uGrade: { value: grade ? 1 : 0 },
      uAccent: { value: accent ? 1 : 0 },
      uAspect: { value: aspect() },
      uGlowPos: { value: new THREE.Vector2(0.5, 0.5) },
      uGlowRadius: { value: 0.05 },
      uGlowStrength: { value: 0.2 },
      uSeed: { value: 0.37 },
      uEdge: { value: 0 },
      uNight: { value: 0 },
      uFill: { value: new THREE.Vector3() },
      uTexel: { value: new THREE.Vector2(1 / size.x, 1 / size.y) },
    },
    vertexShader: /* glsl */`
      varying vec2 vUv;
      void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: /* glsl */`
      uniform sampler2D tDiffuse, tMask;
      uniform float uGrade, uAccent, uAspect, uGlowRadius, uGlowStrength, uSeed, uEdge, uNight;
      uniform vec2 uGlowPos, uTexel;
      uniform vec3 uFill;
      varying vec2 vUv;
      float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
      void main() {
        vec3 c = texture2D(tDiffuse, vUv).rgb;
        vec4 mask = texture2D(tMask, vUv);
        float m = mask.r * uAccent;
        float self = mask.g;
        float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
        float g = mix(l, smoothstep(0.0, 1.0, l), 0.45);
        vec3 col = mix(c, vec3(g), uGrade * (1.0 - m));
        // 夜里：月光下带一点冷（银蓝），点缀色不动
        col = mix(col, col * vec3(0.84, 0.9, 1.0), uNight * 0.7 * (1.0 - m));
        vec2 d = vUv - uGlowPos; d.x *= uAspect;
        float r2 = dot(d, d) / (uGlowRadius * uGlowRadius);
        // 光属于雅典娜：夜里她的光更强、更暖，是夜城里唯一的亮处
        col += uGlowStrength * (1.0 + 0.9 * uNight) * exp(-r2 * 1.2) * (1.0 - 0.92 * self) * mix(vec3(1.0), vec3(1.0, 0.95, 0.84), uNight);
        col += self * (0.025 + 0.05 * uNight);
        vec2 q = vUv - 0.5; q.x *= uAspect;
        col *= mix(1.0, 0.84 - 0.14 * uNight, smoothstep(0.5, 1.15, length(q)));
        col += (hash12(vUv * vec2(1777.0, 1009.0) + uSeed) - 0.5) * 0.028;
        // 地图外：主题的纯色，不调色、不加暗角和颗粒（输出已是显示空间，uFill 就是那个十六进制值）。遮罩没有抗锯齿，
        // 取五个点的平均让边缘软一两个像素
        float outside = (mask.b
          + texture2D(tMask, vUv + vec2(uTexel.x, 0.0)).b + texture2D(tMask, vUv - vec2(uTexel.x, 0.0)).b
          + texture2D(tMask, vUv + vec2(0.0, uTexel.y)).b + texture2D(tMask, vUv - vec2(0.0, uTexel.y)).b) / 5.0;
        col = mix(clamp(col, 0.0, 1.0), uFill, outside);
        gl_FragColor = vec4(col, 1.0);
      }`,
  });
  // ShaderPass 构造时会克隆 uniforms，渲染目标的贴图不能被克隆（会被置空），所以构造之后再挂上（打样踩过）
  gradePass.uniforms.tMask.value = maskTarget.texture;
  composer.addPass(gradePass);

  function resize() {
    const w = window.innerWidth, h = window.innerHeight;
    renderer.setSize(w, h, false);
    composer.setSize(w, h);
    renderer.getDrawingBufferSize(size);
    maskTarget.setSize(size.x, size.y);
    gtao.setSize(size.x, size.y);
    gradePass.uniforms.uAspect.value = aspect();
    gradePass.uniforms.uTexel.value.set(1 / size.x, 1 / size.y);
    applyFrustum();
  }

  /** 相机和场景的矩阵对齐到这一帧：之后算的每个屏幕空间位置都和渲染用的一致。 */
  function syncMatrices() {
    camera.updateMatrixWorld();
    scene.updateMatrixWorld();
  }

  /** 世界坐标 → 绘制缓冲区里的 UV（0..1，左下为原点）。调用前必须 syncMatrices()。 */
  const tmp = new THREE.Vector3();
  function projectUv(world) {
    tmp.copy(world).project(camera);
    return { x: (tmp.x + 1) / 2, y: (tmp.y + 1) / 2, z: tmp.z };
  }

  /** 世界坐标 → CSS 像素（左上为原点），给 2D 叠加层用。 */
  function projectCss(world) {
    const uv = projectUv(world);
    return { x: uv.x * window.innerWidth, y: (1 - uv.y) * window.innerHeight, z: uv.z };
  }

  function placeSun(target) {
    // 太阳从镜头左前方照来：正面受光、右侧面落在阴影里。阴影相机跟着视野中心走，按贴图格子对齐防止闪烁。
    const texel = (sc.right - sc.left) / sun.shadow.mapSize.x;
    const tx = Math.round(target.x / texel) * texel, tz = Math.round(target.z / texel) * texel;
    sun.target.position.set(tx, 0, tz);
    sun.position.set(tx, 0, tz).add(new THREE.Vector3().setFromSphericalCoords(140, THREE.MathUtils.degToRad(90 - 54), THREE.MathUtils.degToRad(-38)));
    sun.target.updateMatrixWorld();
  }

  function render() {
    renderMask();
    composer.render();
  }

  // 夜里（M3 第三轮：跟着应用的深色主题）：天色压暗、月光（冷、暗）代替日光、环境光收着、曝光略提；
  // 雅典娜的光在调色里加强（uNight）。k 从 0（白天）到 1（夜里）连续变，主题切换时 game 用它做一段过渡
  // 白天的天色是地中海正午的淡蓝（黑白时代是暖灰 0xc9c4bb）；地面反上来的光带一点赭黄的土色
  const DAY = { bg: new THREE.Color(0xbcd2e2), sky: new THREE.Color(0xdfeaf5), ground: new THREE.Color(0x9a8466), hemi: 0.55, sun: new THREE.Color(0xfff2df), sunI: 3.6, env: 0.18, exposure: 0.82 };
  const NIGHT = { bg: new THREE.Color(0x111418), sky: new THREE.Color(0x7f8ea5), ground: new THREE.Color(0x15181c), hemi: 0.19, sun: new THREE.Color(0xc4d2e6), sunI: 0.78, env: 0.05, exposure: 0.92 };
  const rgb = (hex) => [(hex >> 16) & 255, (hex >> 8) & 255, hex & 255].map((v) => v / 255);
  const FILL_RGB = { day: rgb(FILL.day), night: rgb(FILL.night) };
  let night = 0;
  function setNight(k) {
    if (!Number.isFinite(k)) throw new TypeError('render: night must be a number');
    night = Math.min(1, Math.max(0, k));
    const lerp = (a, b) => a + (b - a) * night;
    if (scene.background?.isColor) scene.background.copy(DAY.bg).lerp(NIGHT.bg, night);
    hemi.color.copy(DAY.sky).lerp(NIGHT.sky, night);
    hemi.groundColor.copy(DAY.ground).lerp(NIGHT.ground, night);
    hemi.intensity = lerp(DAY.hemi, NIGHT.hemi);
    sun.color.copy(DAY.sun).lerp(NIGHT.sun, night);
    sun.intensity = lerp(DAY.sunI, NIGHT.sunI);
    scene.environmentIntensity = lerp(DAY.env, NIGHT.env);
    renderer.toneMappingExposure = lerp(DAY.exposure, NIGHT.exposure);
    gradePass.uniforms.uNight.value = night;
    fill = FILL_RGB.day.map((d, i) => d + (FILL_RGB.night[i] - d) * night);
    gradePass.uniforms.uFill.value.set(...fill);
  }
  let fill = FILL_RGB.day;
  setNight(0);

  return {
    renderer, scene, camera, composer, gradePass, gtao, sun, hemi, size,
    applyFrustum, resize, syncMatrices, projectUv, projectCss, placeSun, render, setNight,
    get night() { return night; },
    /** 地图外此刻的纯色（显示空间的 0..1 分量）：测试与截图核对用。 */
    get fill() { return [...fill]; },
  };
}
