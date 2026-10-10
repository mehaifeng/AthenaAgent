// 交到你手上的成果（设计稿 7.1"收"）：报告是卷轴，表格是账册，图片是彩绘板；回答本身也是一卷。
//   - 待处理：放在地上一块圆石座上，带点缀色和一圈一胀一缩的白环（"轮到你了"；红绿色弱的人看红色和深灰差别不大，
//     所以同时有亮度对比和脉动，第 8 节）。收下之后颜色褪进城邦（调用方把 userData.accent 关掉）。
//   - 陈列：收下的藏品摆在场馆的底座上（7.2），没有颜色、没有白环，比在地上时小一号，站在底座顶面上。
import * as THREE from '../../vendor/three.bundle.mjs';

const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };
const mesh = (g, m) => shadowed(new THREE.Mesh(g, m));

/** 成果本身（不带石座和白环），原点在底面中心。accent：待处理的那一件留色。 */
export function buildItemBody(M, kind = 'scroll', { accent = true } = {}) {
  const g = new THREE.Group();
  const mark = (m) => { if (accent) m.userData.accent = true; return m; };
  if (kind === 'ledger') {
    // 账册：一本合起来的厚册子，封皮留色，书页是纸色
    const cover = mark(mesh(new THREE.BoxGeometry(0.62, 0.16, 0.44), accent ? M.accent : M.darkWood));
    cover.position.y = 0.09;
    g.add(cover);
    const pages = mesh(new THREE.BoxGeometry(0.58, 0.12, 0.46), M.papyrus);
    pages.position.set(0.02, 0.09, 0);
    g.add(pages);
    return g;
  }
  if (kind === 'painting') {
    // 彩绘板：斜靠在小木架上的一块板，画框留色
    const easel = mesh(new THREE.BoxGeometry(0.06, 0.7, 0.06), M.wood);
    easel.position.set(0, 0.32, -0.16);
    easel.rotation.x = -0.25;
    g.add(easel);
    const frame = mark(mesh(new THREE.BoxGeometry(0.66, 0.5, 0.05), accent ? M.accent : M.darkWood));
    frame.position.set(0, 0.4, -0.04);
    frame.rotation.x = -0.22;
    g.add(frame);
    const panel = mesh(new THREE.PlaneGeometry(0.56, 0.4), M.papyrus);
    panel.position.set(0, 0.4, -0.01);
    panel.rotation.x = -0.22;
    g.add(panel);
    // 板上几笔：一个人形的剪影（黑白里也看得出"这是一幅画"）
    const figure = mesh(new THREE.PlaneGeometry(0.08, 0.22), M.glaze);
    figure.position.set(-0.08, 0.38, 0.0);
    figure.rotation.x = -0.22;
    g.add(figure);
    return g;
  }
  // 卷轴 / 回答：一卷红色的卷轴（点缀色），两端纸色的轴头，中间一道系绳
  const rod = mark(mesh(new THREE.CylinderGeometry(0.11, 0.11, 0.78, 18), accent ? M.accent : M.papyrus));
  rod.rotation.z = Math.PI / 2;
  rod.position.y = 0.12;
  g.add(rod);
  for (const x of [-0.42, 0.42]) {
    const knob = mesh(new THREE.CylinderGeometry(0.035, 0.035, 0.08, 10), accent ? M.papyrus : M.darkWood);
    knob.rotation.z = Math.PI / 2;
    knob.position.set(x, 0.12, 0);
    g.add(knob);
  }
  const tie = mesh(new THREE.TorusGeometry(0.115, 0.016, 6, 18), accent ? M.papyrus : M.glaze);
  tie.rotation.y = Math.PI / 2;
  tie.position.y = 0.12;
  g.add(tie);
  if (kind === 'answer') {
    // 回答：卷轴上多一道封带，和写进工作区的报告分开
    const band = mesh(new THREE.TorusGeometry(0.113, 0.012, 6, 18), accent ? M.papyrus : M.glaze);
    band.rotation.y = Math.PI / 2;
    band.position.set(0.22, 0.12, 0);
    g.add(band);
  }
  return g;
}

/**
 * 地上等你收下的成果：石座 + 成果 + 白环。userData.ring 是白环，userData.body 是成果本身
 * （遗失时只剩石座：调用方把 body 藏起来）。
 */
export function buildDeliveredItem(M, kind = 'scroll') {
  const g = new THREE.Group();
  const base = mesh(new THREE.CylinderGeometry(0.46, 0.5, 0.1, 24), M.marble);
  base.position.y = 0.05;
  g.add(base);
  const body = buildItemBody(M, kind, { accent: true });
  body.position.y = 0.1;
  g.add(body);
  const ring = new THREE.Mesh(new THREE.TorusGeometry(0.8, 0.045, 8, 48), new THREE.MeshBasicMaterial({ color: 0xffffff }));
  ring.rotation.x = Math.PI / 2;
  ring.position.y = 0.03;
  g.add(ring);
  g.userData.ring = ring;
  g.userData.body = body;
  return g;
}

/** 陈列在底座上的藏品（不带颜色、不带白环）；底座顶面在 y = 0.8。 */
export function buildDisplayedItem(M, kind = 'scroll') {
  const g = new THREE.Group();
  const body = buildItemBody(M, kind, { accent: false });
  body.scale.setScalar(kind === 'painting' ? 0.9 : 0.72);
  body.position.y = 0.8;
  g.add(body);
  g.userData.body = body;
  return g;
}

/** 摆放时空位上的白环（一胀一缩）：选位次时才出现，不带颜色——这是你在做的事，不是"轮到你了"。 */
export function buildSlotMarker() {
  const ring = new THREE.Mesh(new THREE.TorusGeometry(0.55, 0.035, 8, 40), new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.9 }));
  ring.rotation.x = Math.PI / 2;
  ring.position.y = 0.86;
  return ring;
}
