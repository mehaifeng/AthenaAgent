// 海：一块细分过的平面，顶点上叠几道缓慢的涌浪（Gerstner 式的方向正弦波，只抬高度——城里的海在码头边上，浪不能大），
// 片元里再叠两层朝不同方向流动的细波纹（同一张周期噪声贴图，两个尺度），法线每帧按时间算出来，只用来给波面一点明暗起伏。
// 颜色按离岸的远近从浅水的青绿过渡到深水的蓝；岸线、栈桥、船身周围一圈会涨落的白沫。
// 水面不反光（2026-10-12，用户：水体不需要加光照反射——第一版粗糙度 0.1 加菲涅耳映天，近看满海都是白花花的高光碎片）：
// 粗糙度 1、不吃环境贴图、没有映天，太阳只照出漫反射的明暗。
//
// 用的是 MeshStandardMaterial + onBeforeCompile：光照、阴影、雾、色调映射都沿用 three.js 的标准管线，
// 只改法线、底色和最后加一层天光。时间、天色、船的位置都是 uniform，game 每帧经 water.userData.water.update(...) 推进。
import * as THREE from '../../vendor/three.bundle.mjs';

/** 顶点上的涌浪：方向（单位向量）、波长（米）、振幅（米）、速度（米/秒）。振幅合起来不到 0.1 米：码头和栈桥的石头不会被淹。 */
export const SWELLS = Object.freeze([
  Object.freeze({ dir: [0.28, 0.96], length: 9.0, amp: 0.045, speed: 1.6 }),
  Object.freeze({ dir: [-0.55, 0.83], length: 5.3, amp: 0.025, speed: 1.2 }),
  Object.freeze({ dir: [0.86, 0.5], length: 3.1, amp: 0.012, speed: 0.9 }),
]);

/** 某一点、某一时刻涌浪的高度（米）。船的起伏用同一个函数，船才和它下面的水一起动。 */
export function swellHeight(x, z, t) {
  let h = 0;
  for (const w of SWELLS) {
    const k = (Math.PI * 2) / w.length;
    h += w.amp * Math.sin((w.dir[0] * x + w.dir[1] * z) * k - t * w.speed * k);
  }
  return h;
}

/** 岸的矩形最多几个（着色器里的数组长度）。 */
export const MAX_RECTS = 8;

const GLSL_SWELLS = SWELLS.map((w) => {
  const k = (Math.PI * 2) / w.length;
  return `  wave(p, t, vec2(${w.dir[0].toFixed(4)}, ${w.dir[1].toFixed(4)}), ${k.toFixed(5)}, ${w.amp.toFixed(4)}, ${(w.speed * k).toFixed(5)}, h, g);`;
}).join('\n');

const COMMON = /* glsl */`
uniform float uTime;
varying vec3 vWaterWorld;
void wave(vec2 p, float t, vec2 dir, float k, float amp, float omega, inout float h, inout vec2 g) {
  float ph = dot(dir, p) * k - t * omega;
  h += amp * sin(ph);
  g += amp * k * cos(ph) * dir;
}
void swell(vec2 p, float t, out float h, out vec2 g) {
  h = 0.0; g = vec2(0.0);
${GLSL_SWELLS}
}
`;

/**
 * @param {THREE.Texture} ripple 周期噪声的法线贴图（materials 里的 waveNormal，平铺）
 * @returns {THREE.MeshStandardMaterial} userData.water = { uniforms, update({ time, night, boat }) }
 */
export function makeWaterMaterial(ripple) {
  const tex = ripple.clone();
  tex.repeat.set(1, 1);
  tex.needsUpdate = true;
  const uniforms = {
    uTime: { value: 0 },
    uRipple: { value: tex },
    // 岸（世界坐标的矩形：minX, minZ, maxX, maxZ——码头石岸、栈桥、伸进海里的台地）和船（x, z, 半宽, 半长）：
    // 白沫绕着它们，离它们越近水越浅（青绿）
    uRects: { value: Array.from({ length: MAX_RECTS }, () => new THREE.Vector4(1e6, 1e6, 1e6, 1e6)) },
    uRectCount: { value: 0 },
    // 船在水线上的半宽、半长（船身是深 V，水线处比甲板窄得多：甲板 1.06 × 4.4，水线约 0.7 × 3.6）
    uBoat: { value: new THREE.Vector4(1e6, 1e6, 0.7, 3.6) },
    uBoatSpeed: { value: 0 },
    uShallow: { value: new THREE.Color(0x3fa7a3) },
    uDeep: { value: new THREE.Color(0x1d4f7a) },
    uNight: { value: 0 },
  };
  const DAY_SHALLOW = new THREE.Color(0x3fa7a3), NIGHT_SHALLOW = new THREE.Color(0x1b3c46);
  const DAY_DEEP = new THREE.Color(0x1d4f7a), NIGHT_DEEP = new THREE.Color(0x0c1a2c);

  const m = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 1, metalness: 0, envMapIntensity: 0 });
  m.onBeforeCompile = (shader) => {
    Object.assign(shader.uniforms, uniforms);
    shader.vertexShader = COMMON + shader.vertexShader
      .replace('#include <beginnormal_vertex>', /* glsl */`
        vec3 wpos0 = (modelMatrix * vec4(position, 1.0)).xyz;
        float sh; vec2 sg;
        swell(wpos0.xz, uTime, sh, sg);
        vec3 objectNormal = normalize(vec3(-sg.x, 1.0, -sg.y));
        #ifdef USE_TANGENT
          vec3 objectTangent = vec3(tangent.xyz);
        #endif`)
      .replace('#include <begin_vertex>', /* glsl */`
        vec3 transformed = vec3(position);
        transformed.y += sh;
        vWaterWorld = vec3(wpos0.x, wpos0.y + sh, wpos0.z);`);
    shader.fragmentShader = COMMON + /* glsl */`
uniform sampler2D uRipple;
uniform float uBoatSpeed, uNight;
uniform vec4 uRects[${MAX_RECTS}];
uniform int uRectCount;
uniform vec4 uBoat;
uniform vec3 uShallow, uDeep;
float waterFoam = 0.0;
float waterCrest = 0.0;
vec3 rippleAt(vec2 uv) { vec3 n = texture2D(uRipple, uv).xyz * 2.0 - 1.0; return vec3(n.x, n.z, n.y); }
float sdBox(vec2 p, vec2 c, vec2 h) { vec2 d = abs(p - c) - h; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0); }
float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
float vnoise(vec2 p) {
  vec2 i = floor(p), f = fract(p); vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x), mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x), u.y);
}
float fbm3(vec2 p) { return 0.5 * vnoise(p) + 0.3 * vnoise(p * 2.03 + 7.1) + 0.2 * vnoise(p * 4.11 - 3.7); }
// 碎浪：只进法线、不抬顶点的一组短波（0.9–2.6 米），给波面一点漫反射的明暗
void chop(vec2 p, float t, inout float h, inout vec2 g) {
  wave(p, t, vec2(0.9701, 0.2425), 2.4166, 0.030, 3.2, h, g);
  wave(p, t, vec2(-0.4472, 0.8944), 3.4907, 0.020, 4.1, h, g);
  wave(p, t, vec2(0.6, 0.8), 4.8332, 0.013, 5.0, h, g);
  wave(p, t, vec2(-0.8944, 0.4472), 6.2832, 0.010, 6.3, h, g);
  wave(p, t, vec2(0.1961, 0.9806), 6.9813, 0.008, 6.6, h, g);
}
/** 离最近的岸（码头石岸、栈桥、场馆的台地——都是世界坐标里的矩形）有多远，米。 */
float shoreDistance(vec2 p) {
  float d = 1e5;
  for (int i = 0; i < ${MAX_RECTS}; i++) {
    if (i >= uRectCount) break;
    vec4 r = uRects[i];
    d = min(d, sdBox(p, (r.xy + r.zw) * 0.5, (r.zw - r.xy) * 0.5));
  }
  return d;
}
` + shader.fragmentShader
      .replace('#include <color_fragment>', /* glsl */`
        #include <color_fragment>
        {
          vec2 p = vWaterWorld.xz;
          float sd = shoreDistance(p);
          float edge = max(0.0, sd);
          diffuseColor.rgb = mix(uDeep, uShallow, exp(-edge / 6.0));
          // 浪峰亮一点、浪谷暗一点（水的体积感：光在浪峰里透出来）
          float ch = 0.0; vec2 cg = vec2(0.0);
          swell(p, uTime, ch, cg);
          chop(p, uTime, ch, cg);
          waterCrest = smoothstep(-0.05, 0.08, ch);
          diffuseColor.rgb *= 0.86 + 0.28 * waterCrest;
          // 白沫：贴着岸、栈桥、船身的一窄条，一涨一落地往外推（lap），被噪声撕成一片片；船开动时身后一道 V 形尾迹
          float n = fbm3(p * 2.3 + vec2(uTime * 0.21, -uTime * 0.13));
          float lap = 0.5 + 0.5 * sin(uTime * 1.15 - edge * 3.0 + n * 2.5);
          // 只在岸外一圈：岸里面（栈桥、船身下面）不起沫——斜看时甲板底下那一片水露得出来，第一版那里是一整块白
          float band = step(0.0, sd) * (1.0 - smoothstep(0.0, 0.16 + 0.34 * lap, edge));
          vec2 bq = (p - uBoat.xy) / uBoat.zw;
          // 到椭圆的距离按梯度归一（length(bq) − 1 除以它的梯度长度）：直接乘半宽，船头船尾的白沫会宽出四倍，成一大团
          float bl = max(length(bq), 1e-4);
          float dBoat = (bl - 1.0) / length((bq / bl) / uBoat.zw);
          band = max(band, step(0.0, dBoat) * (1.0 - smoothstep(0.0, 0.12 + 0.2 * lap, dBoat)) * 0.9);
          float behind = (uBoat.y - p.y) * sign(uBoatSpeed);
          float spread = abs(abs(p.x - uBoat.x) - behind * 0.32);
          float wake = abs(uBoatSpeed) * step(0.0, behind) * (1.0 - smoothstep(6.0, 26.0, behind))
            * max(1.0 - smoothstep(0.0, 0.5 + behind * 0.04, spread), (1.0 - smoothstep(0.0, 1.2, abs(p.x - uBoat.x))) * (1.0 - smoothstep(0.0, 8.0, behind)));
          float froth = max(band, wake);
          waterFoam = smoothstep(0.55, 0.8, froth + (n - 0.5) * 1.1) * step(0.02, froth);
          diffuseColor.rgb = mix(diffuseColor.rgb, vec3(0.93, 0.95, 0.96) * (1.0 - 0.55 * uNight), waterFoam);
        }`)
      .replace('#include <normal_fragment_maps>', /* glsl */`
        #include <normal_fragment_maps>
        {
          vec2 p = vWaterWorld.xz;
          float sh = 0.0; vec2 sg = vec2(0.0);
          swell(p, uTime, sh, sg);
          chop(p, uTime, sh, sg);
          vec3 r1 = rippleAt(p * 0.13 + vec2(uTime * 0.017, uTime * 0.031));
          vec3 r2 = rippleAt(p * 0.31 + vec2(-uTime * 0.026, uTime * 0.012));
          vec3 nW = normalize(vec3(-sg.x, 1.0, -sg.y) + vec3(r1.x + r2.x, 0.0, r1.z + r2.z) * 0.5);
          nW = normalize(mix(nW, vec3(0.0, 1.0, 0.0), waterFoam * 0.7));
          normal = normalize((viewMatrix * vec4(nW, 0.0)).xyz);
        }`);
  };
  m.customProgramCacheKey = () => 'polis-water-v2';

  m.userData.water = {
    uniforms,
    /**
     * @param {{ time?: number, night?: number, boat?: { x: number, z: number, speed?: number }, shores?: number[][] }} s
     * time 秒；night 0..1；boat 船的世界位置与速度（米/秒，朝 +z 为正）；shores 岸的矩形 [minX, minZ, maxX, maxZ]，最多 MAX_RECTS 个
     */
    update(s) {
      if (s.time != null) uniforms.uTime.value = s.time;
      if (s.shores) {
        if (s.shores.length > MAX_RECTS) throw new Error(`water: ${s.shores.length} shore rectangles, at most ${MAX_RECTS}`);
        s.shores.forEach((r, i) => uniforms.uRects.value[i].set(...r));
        uniforms.uRectCount.value = s.shores.length;
      }
      if (s.boat) {
        uniforms.uBoat.value.x = s.boat.x;
        uniforms.uBoat.value.y = s.boat.z;
        uniforms.uBoatSpeed.value = Math.max(-1, Math.min(1, (s.boat.speed ?? 0) / 4));
      }
      if (s.night != null) {
        const k = Math.min(1, Math.max(0, s.night));
        uniforms.uNight.value = k;
        uniforms.uShallow.value.copy(DAY_SHALLOW).lerp(NIGHT_SHALLOW, k);
        uniforms.uDeep.value.copy(DAY_DEEP).lerp(NIGHT_DEEP, k);
      }
    },
  };
  return m;
}
