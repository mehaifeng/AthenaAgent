// 渲染：正交相机 + 后期（环境光遮蔽 → 色调映射 → 黑白调色，只给标记物体留色 + 雅典娜的光 + 暗角 + 颗粒），取自打样。
//
// 屏幕空间效果（光晕、旁白气泡、建筑名牌）必须用"这一帧真正用来渲染的"相机矩阵投影（设计稿 12.2）：
// three.js 的 lookAt（OrbitControls 每帧都调）会先用"新位置 + 旧朝向"刷新矩阵，直到 render 才改正。
// 所以每帧的顺序固定为：移动相机与人物 → syncMatrices() → 计算所有屏幕空间位置 → 渲染。
import * as THREE from '../vendor/three.bundle.mjs';
import { EffectComposer, RenderPass, GTAOPass, OutputPass, ShaderPass, RoomEnvironment } from '../vendor/three.bundle.mjs';

export function createRenderer({ canvas, ao = true, shadows = true, grade = true, accent = true }) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setSize(window.innerWidth, window.innerHeight, false);
  renderer.shadowMap.enabled = shadows;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 0.82;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0xc9c4bb);
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  scene.environmentIntensity = 0.18;

  const hemi = new THREE.HemisphereLight(0xe8eef3, 0x8a7f70, 0.55);
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
  const MASK_ACCENT = new THREE.MeshBasicMaterial({ color: 0xff0000 });
  const MASK_ATHENA = new THREE.MeshBasicMaterial({ color: 0x00ff00 });
  const MASK_OFF = new THREE.MeshBasicMaterial({ color: 0x000000 });
  const black = new THREE.Color(0x000000);

  function renderMask() {
    const saved = new Map();
    scene.traverse((o) => {
      if (o.isMesh) { saved.set(o, o.material); o.material = o.userData.accent ? MASK_ACCENT : o.userData.athena ? MASK_ATHENA : MASK_OFF; }
    });
    const bg = scene.background, env = scene.environment;
    scene.background = black;
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
    },
    vertexShader: /* glsl */`
      varying vec2 vUv;
      void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: /* glsl */`
      uniform sampler2D tDiffuse, tMask;
      uniform float uGrade, uAccent, uAspect, uGlowRadius, uGlowStrength, uSeed, uEdge;
      uniform vec2 uGlowPos;
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
        vec2 d = vUv - uGlowPos; d.x *= uAspect;
        float r2 = dot(d, d) / (uGlowRadius * uGlowRadius);
        col += uGlowStrength * exp(-r2 * 1.2) * (1.0 - 0.92 * self) * vec3(1.0);
        col += self * 0.025;
        vec2 q = vUv - 0.5; q.x *= uAspect;
        col *= mix(1.0, 0.84, smoothstep(0.5, 1.15, length(q)));
        col += (hash12(vUv * vec2(1777.0, 1009.0) + uSeed) - 0.5) * 0.028;
        gl_FragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
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

  return {
    renderer, scene, camera, composer, gradePass, gtao, sun, size,
    applyFrustum, resize, syncMatrices, projectUv, projectCss, placeSun, render,
  };
}
