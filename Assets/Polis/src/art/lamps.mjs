// 夜里的灯火（2026-10-13，用户：灯火材质太僵硬，没有散射，外面的街道没有被打亮，柱廊的灯笼只是两个发光几何体）。
//
// 三件事，各管一层：
// 1. 灯火本身（lampGlass）：窗洞、门洞、灯笼的角片不再是一块均匀的自发光——油灯在屋里偏下的地方，光从那里往外弱下去，
//    窗洞四周的墙厚挡掉一圈，窗上有一道木格，门半掩着；每一处的火苗各自轻轻跳（相位编在 UV 的整数部分里，见 glassUv）。
// 2. 照到外面的光（lampField + lampLit）：每一处灯火登记成一个光源（kit.light），整座城的光源在建城时烘成一张俯视的
//    "光照图"——门窗朝外投一片暖光、灯笼向四周投一圈——所有受光的材质在着色时按自己的世界坐标读它，乘上自己的底色，
//    加到自发光里。街面、墙脚、路过的人都被照亮，墙后（光源背面）不亮，高过灯的屋顶也不亮。几百盏灯不用几百个点光源：
//    点光源每多一个，每个片元都要多算一遍，而且灯数一变全部着色器重编译。
// 3. 散射：后期里的辉光（render.mjs 的 glow），只在夜里开。
import * as THREE from '../../vendor/three.bundle.mjs';

/** 光照图的每个像素至少覆盖多少米、最大边长：0.25 米一格，大城封顶 1024。 */
export const LAMP_TEXEL = 0.25;
export const LAMP_MAX_RES = 1024;
/** 光照图 R 通道满格对应的光强（几盏灯叠在一起时不至于一下子顶满）；G 通道是那里光源的平均高度 / LAMP_HEIGHT_RANGE。 */
export const LAMP_FULL = 2;
export const LAMP_HEIGHT_RANGE = 8;

/** 几类灯火的默认强度与照多远（米）。门比窗亮，挂出来的灯笼最亮、照得最远。 */
export const LAMP_KINDS = Object.freeze({
  door: { power: 1.0, radius: 5.4 },
  window: { power: 0.5, radius: 3.8 },
  lantern: { power: 1.05, radius: 6.6 },
});

/**
 * 一处灯火对地面上 (qx, qz) 的贡献（无量纲）。朝外的灯（门窗：n 非零）光心往外挪、背面截断；灯笼四周一样。
 * 衰减是加窗的平方反比：中间一团亮，到 radius 正好落到 0，不会在边上留一道硬边。
 */
export function lampContribution(l, qx, qz) {
  const facing = l.nx !== 0 || l.nz !== 0;
  const off = facing ? l.radius * 0.32 : 0;
  const cx = l.x + l.nx * off, cz = l.z + l.nz * off;
  const d2 = ((qx - cx) ** 2 + (qz - cz) ** 2) / (l.radius * l.radius);
  if (d2 >= 1) return 0;
  const win = (1 - d2) * (1 - d2);
  let f = win / (1 + 3 * d2);
  if (facing) {
    const s = (qx - l.x) * l.nx + (qz - l.z) * l.nz;   // 在墙外多远：墙里（屋里）不亮
    f *= smoothstep(-0.3, 0.4, s);
  }
  return l.power * f;
}

const smoothstep = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

/**
 * 把一组光源（世界坐标：x, y, z, nx, nz, power, radius）烘成光照图。rect = { minX, maxX, minZ, maxZ }。
 * 返回 RGBA 字节：R = 光强 / LAMP_FULL，G = 平均光源高度 / LAMP_HEIGHT_RANGE；行沿 z 增大、列沿 x 增大。纯函数，Node 里可测。
 */
export function lampField(lights, rect, { texel = LAMP_TEXEL, maxRes = LAMP_MAX_RES } = {}) {
  const w = rect.maxX - rect.minX, d = rect.maxZ - rect.minZ;
  if (!(w > 0 && d > 0)) throw new Error('lampField: the rectangle is empty');
  const width = Math.max(2, Math.min(maxRes, Math.ceil(w / texel)));
  const height = Math.max(2, Math.min(maxRes, Math.ceil(d / texel)));
  const sx = w / width, sz = d / height;
  const power = new Float32Array(width * height), lift = new Float32Array(width * height);
  for (const l of lights) {
    for (const k of ['x', 'y', 'z', 'nx', 'nz', 'power', 'radius']) if (!Number.isFinite(l[k])) throw new TypeError(`lampField: light.${k} is not a number`);
    const reach = l.radius * 1.35;
    const i0 = Math.max(0, Math.floor((l.x - reach - rect.minX) / sx)), i1 = Math.min(width - 1, Math.ceil((l.x + reach - rect.minX) / sx));
    const j0 = Math.max(0, Math.floor((l.z - reach - rect.minZ) / sz)), j1 = Math.min(height - 1, Math.ceil((l.z + reach - rect.minZ) / sz));
    for (let j = j0; j <= j1; j++) {
      const qz = rect.minZ + (j + 0.5) * sz;
      for (let i = i0; i <= i1; i++) {
        const c = lampContribution(l, rect.minX + (i + 0.5) * sx, qz);
        if (c <= 0) continue;
        power[j * width + i] += c;
        lift[j * width + i] += c * l.y;
      }
    }
  }
  const data = new Uint8Array(width * height * 4);
  for (let p = 0; p < width * height; p++) {
    data[p * 4] = Math.round(255 * Math.min(1, power[p] / LAMP_FULL));
    data[p * 4 + 1] = power[p] > 0 ? Math.round(255 * Math.min(1, lift[p] / power[p] / LAMP_HEIGHT_RANGE)) : 0;
    data[p * 4 + 3] = 255;
  }
  return { data, width, height, rect: { ...rect } };
}

/** 读回光照图在 (x, z) 的光强（0..LAMP_FULL，最近的像素）：测试用。 */
export function lampAt(field, x, z) {
  const { rect, width, height, data } = field;
  const i = Math.floor((x - rect.minX) / (rect.maxX - rect.minX) * width), j = Math.floor((z - rect.minZ) / (rect.maxZ - rect.minZ) * height);
  if (i < 0 || j < 0 || i >= width || j >= height) return 0;
  return data[(j * width + i) * 4] / 255 * LAMP_FULL;
}

// ———————————————————————— 着色器 ————————————————————————

// 全城共用一份：光照图、它覆盖的矩形、夜色、时间（火苗跳动）。gallery 和测试建的材质也读它，没有光照图时是一张黑图。
const blank = new THREE.DataTexture(new Uint8Array([0, 0, 0, 255]), 1, 1);
blank.needsUpdate = true;
export const LAMP_UNIFORMS = {
  uLampMap: { value: blank },
  uLampRect: { value: new THREE.Vector4(0, 0, 1, 1) },   // minX, minZ, 1/宽, 1/深
  uLampK: { value: 0 },
  uLampTime: { value: 0 },
  uLampColor: { value: new THREE.Color(1.0, 0.62, 0.3) },   // 油灯的暖橙（线性空间）
  uLampStrength: { value: 0.72 },
};

/** 换上一张新的光照图（建城、换掉一座建筑之后）。 */
export function setLampField(field) {
  const old = LAMP_UNIFORMS.uLampMap.value;
  const tex = new THREE.DataTexture(field.data, field.width, field.height, THREE.RGBAFormat, THREE.UnsignedByteType);
  tex.magFilter = THREE.LinearFilter;
  tex.minFilter = THREE.LinearFilter;
  tex.generateMipmaps = false;
  tex.wrapS = tex.wrapT = THREE.ClampToEdgeWrapping;
  tex.flipY = false;
  tex.needsUpdate = true;
  LAMP_UNIFORMS.uLampMap.value = tex;
  const r = field.rect;
  LAMP_UNIFORMS.uLampRect.value.set(r.minX, r.minZ, 1 / (r.maxX - r.minX), 1 / (r.maxZ - r.minZ));
  if (old !== blank) old.dispose();
}

export function setLampNight(k, timeSec) {
  LAMP_UNIFORMS.uLampK.value = Math.min(1, Math.max(0, k));
  if (Number.isFinite(timeSec)) LAMP_UNIFORMS.uLampTime.value = timeSec;
}

/**
 * 每一处 String.replace 都对着 three.js 的一个 chunk 名：chunk 改名时 replace 什么都不做、着色器照样编译——
 * 灯火就悄悄没了（water.mjs 踩过同一类）。所以没替换到就抛错，art.test.mjs 拿 ShaderLib.standard 实际跑一遍。
 */
function must(src, find, replacement, what) {
  if (!src.includes(find)) throw new Error(`lamps: shader chunk "${find}" not found (${what})`);
  return src.replace(find, replacement);
}

const WORLD_VARYING = 'varying vec3 vLampWorld;';
function patchWorld(shader, what) {
  shader.vertexShader = must(shader.vertexShader, '#include <common>', `#include <common>\n${WORLD_VARYING}`, what);
  shader.vertexShader = must(shader.vertexShader, '#include <project_vertex>', `#include <project_vertex>
    vec4 lampWorld = vec4( transformed, 1.0 );
    #ifdef USE_INSTANCING
      lampWorld = instanceMatrix * lampWorld;
    #endif
    vLampWorld = ( modelMatrix * lampWorld ).xyz;`, what);
  shader.fragmentShader = must(shader.fragmentShader, '#include <common>', `#include <common>
    ${WORLD_VARYING}
    uniform sampler2D uLampMap;
    uniform vec4 uLampRect;
    uniform float uLampK, uLampTime, uLampStrength;
    uniform vec3 uLampColor;`, what);
  Object.assign(shader.uniforms, LAMP_UNIFORMS);
}

/** 受灯光照的着色：读光照图，按底色加到自发光里（漫反射的近似：光 × 底色）。 */
export function patchLampLit(shader) {
  patchWorld(shader, 'lamp-lit');
  shader.fragmentShader = must(shader.fragmentShader, '#include <emissivemap_fragment>', `#include <emissivemap_fragment>
    if ( uLampK > 0.0 ) {
      vec2 lampUv = ( vLampWorld.xz - uLampRect.xy ) * uLampRect.zw;
      vec4 lampTexel = texture2D( uLampMap, lampUv );
      float lampInside = step( 0.0, lampUv.x ) * step( lampUv.x, 1.0 ) * step( 0.0, lampUv.y ) * step( lampUv.y, 1.0 );
      // 高过那里的灯就渐渐照不到：屋顶、二楼的檐不被街上的灯照亮
      float lampAbove = vLampWorld.y - lampTexel.g * ${LAMP_HEIGHT_RANGE.toFixed(1)};
      float lampReach = 1.0 - smoothstep( 0.3, 2.2, lampAbove );
      // 光照图是给地面的：竖着的墙是被掠过的，只吃到一部分（不然门边整面墙一片白）
      float lampUp = inverseTransformDirection( normal, viewMatrix ).y;
      float lampFace = mix( 0.3, 1.0, clamp( lampUp, 0.0, 1.0 ) );
      totalEmissiveRadiance += diffuseColor.rgb * uLampColor * ( lampTexel.r * ${LAMP_FULL.toFixed(1)} * uLampStrength * lampReach * lampFace * lampInside * uLampK );
    }`, 'lamp-lit');
}

/**
 * 灯火本身的着色（窗洞、门洞、灯笼的角片）。UV 的小数部分是这块面上的位置（0..1），整数部分编着两件事（glassUv）：
 * u 的整数部分 / 2 是这一处火苗的相位编号；v ≥ 2 表示门洞。emissive 的强度（setNightWindows 设的 emissiveIntensity）是总闸。
 */
export function patchLampGlass(shader, kind) {
  patchWorld(shader, `lamp-glass-${kind}`);
  shader.vertexShader = must(shader.vertexShader, '#include <common>', `#include <common>\nvarying vec2 vGlassUv;`, 'glass');
  shader.vertexShader = must(shader.vertexShader, '#include <uv_vertex>', `#include <uv_vertex>\nvGlassUv = uv;`, 'glass');
  shader.fragmentShader = must(shader.fragmentShader, '#include <common>', `#include <common>\nvarying vec2 vGlassUv;`, 'glass');
  const body = kind === 'pane'
    ? `
      // 灯笼的角片：一圈六片，片与片之间是铜骨（暗），火苗在中间偏下，往上下两头弱下去
      float seg = fract( g.x * 6.0 );
      float rib = smoothstep( 0.0, 0.16, seg ) * smoothstep( 0.0, 0.16, 1.0 - seg );
      float k = ( 0.3 + 1.05 * exp( -pow( ( g.y - 0.42 ) * 2.8, 2.0 ) ) ) * mix( 0.45, 1.0, rib );`
    : `
      // 屋里的油灯在开口中间偏下：那里最亮最黄，往四周弱下去、偏红；开口四周是墙厚挡出来的一圈暗
      float edge = smoothstep( 0.0, 0.16, g.x ) * smoothstep( 0.0, 0.16, 1.0 - g.x ) * smoothstep( 0.0, 0.08, g.y ) * smoothstep( 0.0, 0.14, 1.0 - g.y );
      float hot = exp( -( ( g.x - 0.5 ) * ( g.x - 0.5 ) * 6.0 + ( g.y - 0.3 ) * ( g.y - 0.3 ) * 4.0 ) );
      float k = mix( 0.22, 1.0, edge ) * ( 0.4 + 0.95 * hot );
      if ( isDoor ) {
        // 门半掩着：左边四成是背光的门扇，门缝和门槛那一道最亮
        float leaf = 1.0 - smoothstep( 0.37, 0.41, g.x );
        float sill = 1.0 - smoothstep( 0.0, 0.07, g.y );
        k = k * mix( 1.0, 0.12, leaf ) + 0.35 * sill * ( 1.0 - leaf );
      } else {
        // 窗上一道十字木格
        float bar = max( 1.0 - smoothstep( 0.025, 0.05, abs( g.x - 0.5 ) ), 1.0 - smoothstep( 0.025, 0.05, abs( g.y - 0.56 ) ) );
        k *= 1.0 - 0.88 * bar;
      }`;
  shader.fragmentShader = must(shader.fragmentShader, '#include <emissivemap_fragment>', `#include <emissivemap_fragment>
    {
      vec2 cell = floor( vGlassUv );
      bool isDoor = cell.y >= 2.0;
      vec2 g = vGlassUv - vec2( cell.x, isDoor ? 2.0 : cell.y );
      float phase = fract( sin( cell.x * 12.9898 + 4.1 ) * 43758.5453 );
      ${body}
      // 火苗跳：两个不相干的频率叠在一起，每一处相位不同，幅度只有几个百分点——看得出是活的火，不是在闪
      float flick = 0.93 + 0.05 * sin( uLampTime * ( 5.3 + 3.1 * phase ) + phase * 31.0 ) + 0.03 * sin( uLampTime * ( 13.7 + 6.3 * phase ) + phase * 7.0 );
      vec3 warm = mix( vec3( 0.62, 0.16, 0.02 ), vec3( 1.0, 0.6, 0.26 ), clamp( k, 0.0, 1.0 ) );
      totalEmissiveRadiance = warm * min( k, 1.1 ) * flick * emissive.r;
    }`, `lamp-glass-${kind}`);
}

/** 在一份材质上装补丁：接在原来的 onBeforeCompile 之后，程序缓存键带上补丁名，不和没打补丁的同类材质共用程序。 */
export function installPatch(material, key, patch) {
  const prev = material.onBeforeCompile;
  const prevKey = material.customProgramCacheKey;
  material.onBeforeCompile = function (shader, renderer) { prev.call(this, shader, renderer); patch(shader); };
  material.customProgramCacheKey = function () { return `${prevKey.call(this)}|${key}`; };
  material.userData.lampPatch = key;
  return material;
}

/**
 * 灯火开口的 UV：前后两面的小数部分铺满 0..1（四周的窄面也是，看不出来），整数部分编相位与门。
 * 位置哈希出相位编号（0..15），同一座建筑每次重建都一样。
 */
export function glassUv(geometry, { x, y, z, door = false }) {
  const h = Math.sin(x * 12.9898 + y * 78.233 + z * 37.719) * 43758.5453;
  const cell = Math.floor((h - Math.floor(h)) * 16) * 2;
  const uv = geometry.attributes.uv;
  for (let i = 0; i < uv.count; i++) uv.setXY(i, cell + Math.min(0.999, Math.max(0.001, uv.getX(i))), (door ? 2 : 0) + Math.min(0.999, Math.max(0.001, uv.getY(i))));
  return geometry;
}
