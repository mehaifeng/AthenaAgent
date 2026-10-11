// 测试用的 A-pose 人台（bind.test.mjs 与冒烟测试共用）：圆柱、球、盒子拼成，关节在哪是已知的——仓库里不放第三方模型。
// 1.805 米，面朝 +z，左手在 +x，手臂离开身体 30°，穿一条到小腿的裙子，两脚分开。
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';

export const ARM_ANGLE = THREE.MathUtils.degToRad(30);

/**
 * @param {{ armAngle?: number }} [options] armAngle ≈ 0：手臂贴着身体（不是 A-pose）
 * @returns {{ scene: THREE.Group, truth: { shoulder: object, wrist: object, ankle: object } }} truth 按 sx（+1 左、−1 右）给真实的关节位置
 */
export function mannequin({ armAngle = ARM_ANGLE } = {}) {
  const scene = new THREE.Group();
  const add = (geometry, x, y, z) => { const m = new THREE.Mesh(geometry, new THREE.MeshStandardMaterial()); m.position.set(x, y, z); scene.add(m); return m; };
  const truth = { shoulder: {}, wrist: {}, ankle: {} };
  for (const sx of [1, -1]) {
    add(new THREE.BoxGeometry(0.09, 0.07, 0.24), sx * 0.09, 0.035, 0.04);                       // 脚
    add(new THREE.CylinderGeometry(0.06, 0.045, 0.86, 16), sx * 0.09, 0.07 + 0.43, 0);         // 腿（踝 0.07 到髋 0.93）
    truth.ankle[sx] = new THREE.Vector3(sx * 0.09, 0.07, 0);
    // 手臂：肩关节 (±0.17, 1.47)，往外下方斜 armAngle，上粗下细到手腕，再接一只扁手
    const S = new THREE.Vector3(sx * 0.17, 1.47, 0);
    const d = new THREE.Vector3(sx * Math.sin(armAngle), -Math.cos(armAngle), 0);
    const q = new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, -1, 0), d);
    add(new THREE.CylinderGeometry(0.046, 0.03, 0.52, 16).translate(0, -0.26, 0).applyQuaternion(q), S.x, S.y, S.z);
    const W = S.clone().addScaledVector(d, 0.52);
    add(new THREE.BoxGeometry(0.09, 0.17, 0.03).translate(0, -0.085, 0).applyQuaternion(q), W.x, W.y, W.z);
    truth.shoulder[sx] = S;
    truth.wrist[sx] = W;
  }
  add(new THREE.CylinderGeometry(0.15, 0.21, 0.8, 32), 0, 0.64, 0);                                  // 裙子（下摆 0.24）
  add(new THREE.CylinderGeometry(0.16, 0.15, 0.5, 32).scale(1, 1, 0.7), 0, 1.25, 0);                 // 躯干
  add(new THREE.CapsuleGeometry(0.05, 0.32, 6, 16).rotateZ(Math.PI / 2), 0, 1.47, 0);                // 两肩
  add(new THREE.CylinderGeometry(0.05, 0.055, 0.14, 16), 0, 1.57, 0);                                 // 脖子
  add(new THREE.SphereGeometry(0.105, 24, 16), 0, 1.70, 0);                                           // 头（顶 1.805）
  return { scene, truth };
}

/** 人台写成一个 GLB（一个网格：位置、法线、索引；冒烟测试拿它当外部人物）。 */
export function mannequinGlb(options) {
  const { scene } = mannequin(options);
  scene.updateMatrixWorld(true);
  const positions = [], normals = [], indices = [];
  const p = new THREE.Vector3(), n = new THREE.Vector3(), normalMatrix = new THREE.Matrix3();
  scene.traverse((o) => {
    if (!o.isMesh) return;
    const g = o.geometry, base = positions.length / 3;
    normalMatrix.getNormalMatrix(o.matrixWorld);
    for (let i = 0; i < g.attributes.position.count; i++) {
      p.fromBufferAttribute(g.attributes.position, i).applyMatrix4(o.matrixWorld);
      n.fromBufferAttribute(g.attributes.normal, i).applyMatrix3(normalMatrix).normalize();
      positions.push(p.x, p.y, p.z);
      normals.push(n.x, n.y, n.z);
    }
    const index = g.index;
    for (let i = 0; i < (index ? index.count : g.attributes.position.count); i++) indices.push(base + (index ? index.getX(i) : i));
  });
  const pos = new Float32Array(positions), nor = new Float32Array(normals), idx = new Uint32Array(indices);
  const min = [0, 1, 2].map((a) => Math.min(...pos.filter((_, i) => i % 3 === a)));
  const max = [0, 1, 2].map((a) => Math.max(...pos.filter((_, i) => i % 3 === a)));
  const bin = Buffer.concat([Buffer.from(pos.buffer), Buffer.from(nor.buffer), Buffer.from(idx.buffer)]);
  const json = {
    asset: { version: '2.0', generator: 'athena test mannequin' },
    scene: 0, scenes: [{ nodes: [0] }], nodes: [{ mesh: 0, name: 'mannequin' }],
    meshes: [{ primitives: [{ attributes: { POSITION: 0, NORMAL: 1 }, indices: 2 }] }],
    buffers: [{ byteLength: bin.length }],
    bufferViews: [
      { buffer: 0, byteOffset: 0, byteLength: pos.byteLength },
      { buffer: 0, byteOffset: pos.byteLength, byteLength: nor.byteLength },
      { buffer: 0, byteOffset: pos.byteLength + nor.byteLength, byteLength: idx.byteLength },
    ],
    accessors: [
      { bufferView: 0, componentType: 5126, count: pos.length / 3, type: 'VEC3', min, max },
      { bufferView: 1, componentType: 5126, count: nor.length / 3, type: 'VEC3' },
      { bufferView: 2, componentType: 5125, count: idx.length, type: 'SCALAR' },
    ],
  };
  const pad = (buf, fill) => Buffer.concat([buf, Buffer.alloc((4 - (buf.length % 4)) % 4, fill)]);
  const jsonChunk = pad(Buffer.from(JSON.stringify(json)), 0x20), binChunk = pad(bin, 0);
  const header = Buffer.alloc(12);
  header.writeUInt32LE(0x46546c67, 0); header.writeUInt32LE(2, 4); header.writeUInt32LE(12 + 8 + jsonChunk.length + 8 + binChunk.length, 8);
  const chunk = (buf, type) => { const h = Buffer.alloc(8); h.writeUInt32LE(buf.length, 0); h.writeUInt32LE(type, 4); return Buffer.concat([h, buf]); };
  return Buffer.concat([header, chunk(jsonChunk, 0x4e4f534a), chunk(binChunk, 0x004e4942)]);
}
