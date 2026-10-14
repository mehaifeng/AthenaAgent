// 居民干活的工位（第三轮需求 4）：陶轮、揉面台、石料、锯木架。城里的东西（world.setStation 摆，资产 prop.station-*），不挡路——
// 它们只在干活的人面前半米，挡路了反而把门前那条路堵上（资产登记为不参与碰撞，和成果一样）。原点在地面上、工位的正中，
// "正面"（人站的那一边）朝 −z；大小与 crafts.STATION_EXTENT 一致（crafts.test 量）。
import * as THREE from '../../vendor/three.bundle.mjs';
import { Kit, m4 } from './kit.mjs';

/** 陶坯：一只正在拉的罐子（车削出来的轮廓）。 */
function clayPot() {
  const pts = [[0, 0], [0.07, 0.005], [0.1, 0.05], [0.105, 0.11], [0.085, 0.16], [0.07, 0.19], [0.075, 0.2]];
  return new THREE.LatheGeometry(pts.map(([x, y]) => new THREE.Vector2(x, y)), 16);
}

/** 工位的高度（干活的那一面离地多高）：姿势按它摆手。 */
export const STATION_TOP = Object.freeze({ wheel: 0.79, trough: 0.8, block: 0.6, sawhorse: 0.84, loom: 1.92 });

export function buildStation(M, kind) {
  const kit = new Kit();
  switch (kind) {
    case 'wheel': {
      // 陶轮：一只木架子托着石盘，盘上一只拉到一半的陶坯（湿陶土比烧好的颜色深）；盘面 0.79 米，坯腰 0.9 米
      for (const sx of [-1, 1]) for (const sz of [-1, 1]) kit.box(M.darkWood, 0.05, 0.7, 0.05, sx * 0.18, 0.35, sz * 0.18);
      kit.box(M.wood, 0.46, 0.04, 0.46, 0, 0.72, 0);
      kit.cylinder(M.stone, 0.27, 0.28, 0.05, 0, 0.765, 0, 20);
      kit.add(M.terracotta, clayPot(), m4(0, 0.79, 0));
      kit.cylinder(M.stone, 0.22, 0.24, 0.05, 0, 0.12, 0, 16);   // 脚踢的飞轮
      break;
    }
    case 'trough': {
      // 揉面台：一张齐腰的木台，台上一团面、撒着一层面粉
      kit.box(M.wood, 0.9, 0.06, 0.48, 0, 0.77, 0);
      for (const sx of [-1, 1]) for (const sz of [-1, 1]) kit.box(M.darkWood, 0.06, 0.74, 0.06, sx * 0.38, 0.37, sz * 0.18);
      kit.box(M.papyrus, 0.5, 0.004, 0.3, 0, 0.802, 0);
      kit.add(M.papyrus, new THREE.SphereGeometry(0.11, 12, 8), m4(0, 0.84, 0, 0, 0, 0, 1.3, 0.55, 1));
      break;
    }
    case 'block': {
      // 石料：一块半琢好的方石垫在木托上（顶面 0.6 米），脚边几块碎石片
      kit.box(M.darkWood, 0.6, 0.1, 0.52, 0, 0.05, 0, 0.12);
      kit.box(M.marble, 0.55, 0.5, 0.48, 0, 0.35, 0, 0.12);
      for (let k = 0; k < 5; k++) kit.box(M.marble, 0.06 + k * 0.01, 0.025, 0.05, -0.35 + k * 0.17, 0.013, -0.32 + (k % 2) * 0.06, k);
      break;
    }
    case 'sawhorse': {
      // 锯木架：两副人字腿架着一块木板，锯口在人面前
      for (const sx of [-1, 1]) for (const sz of [-1, 1]) kit.box(M.darkWood, 0.06, 0.8, 0.06, sx * 0.55, 0.38, sz * 0.1, 0, -sz * 0.22, 0);
      for (const sx of [-1, 1]) kit.box(M.darkWood, 0.08, 0.06, 0.3, sx * 0.55, 0.76, 0);
      kit.box(M.wood, 1.5, 0.05, 0.22, 0, 0.815, 0);
      break;
    }
    case 'loom': {
      // 立式经线垂坠织机（第四轮需求 5，古希腊的织机）：两根立柱、顶上一根卷布梁，经线从梁上垂下来，底下坠着一排陶坠子；
      // 布从上往下织：梁下 0.55 米已经织成（布面），再往下是一根根经线，中间一根分经棒。经线面在 z = 0（织工站在 −z 那边，
      // 掌心贴着它，poses.CRAFT_ARMS.weave*）
      for (const sx of [-1, 1]) {
        kit.box(M.darkWood, 0.07, 2.0, 0.07, sx * 0.62, 1.0, 0.04);
        kit.box(M.darkWood, 0.07, 0.07, 0.38, sx * 0.62, 0.035, 0.08);   // 脚
      }
      kit.cylinder(M.wood, 0.05, 0.05, 1.32, 0, 1.92, 0.02, 10, 0, Math.PI / 2);
      // 已经织好的布：一块略带织纹的布面（颜色按工位一律用素麻）
      kit.box(M.awning, 1.08, 0.56, 0.012, 0, 1.6, 0.01);
      for (let k = 0; k < 5; k++) kit.box(M.clothShades[k % M.clothShades.length], 1.08, 0.018, 0.016, 0, 1.38 + k * 0.1, 0.012);   // 几道彩纹
      // 经线：从织口到坠子，一根根细线
      for (let k = 0; k < 28; k++) {
        const x = -0.52 + (k / 27) * 1.04;
        kit.box(M.papyrus, 0.004, 1.02, 0.004, x, 0.81, (k % 2) * 0.012);
        kit.add(M.terracotta, new THREE.ConeGeometry(0.022, 0.07, 4), m4(x, 0.27, (k % 2) * 0.012));   // 陶坠子
      }
      kit.cylinder(M.wood, 0.018, 0.018, 1.2, 0, 0.98, 0.0, 8, 0, Math.PI / 2);   // 分经棒
      kit.cylinder(M.wood, 0.02, 0.02, 1.2, 0, 1.3, -0.01, 8, 0, Math.PI / 2);    // 织口的纬棒
      break;
    }
    default:
      throw new Error(`stations: unknown station '${kind}'`);
  }
  const g = kit.build();
  g.userData.footprint = null;   // 不挡路（见文件头）
  g.userData.station = kind;
  return g;
}
