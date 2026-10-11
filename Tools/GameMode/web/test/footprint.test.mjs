// 碰撞足迹（M3 决定 1）：按网格推出来的足迹，而不是套在外面的方盒子。每条规则一个用例。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import {
  footprintOf, footprintOfGeometry, footprintBlocked, createOccupancy, CELL, PLAYER_RADIUS, BLOCKING_HEIGHT, BODY_BAND, COVER_REACH,
} from '../../../../Assets/Polis/src/footprint.mjs';

const mat = new THREE.MeshStandardMaterial();
const box = (w, h, d, x, y, z) => { const m = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat); m.position.set(x, y, z); return m; };
const group = (...children) => { const g = new THREE.Group(); for (const c of children) g.add(c); return g; };
const blockedAt = (fp, x, z) => !!fp && footprintBlocked(fp, x, z);

test('footprint: a box blocks its own outline plus the player radius, not the plot it stands on', () => {
  const fp = footprintOf(group(box(1, 1, 1, 0, 0.5, 0)));
  assert.ok(fp, '一个 1 米的箱子有足迹');
  assert.ok(blockedAt(fp, 0, 0), '箱子里面');
  assert.ok(blockedAt(fp, 0.5 + PLAYER_RADIUS - 0.05, 0), '贴着箱子、还在人的半径之内');
  assert.ok(!blockedAt(fp, 0.5 + PLAYER_RADIUS + 0.17, 0), '离开一个人的半径（再加一格的容差）就能站');
  assert.ok(!blockedAt(fp, 3, 3), '远处是空的——不是整块地都挡');
  assert.ok(Math.abs(fp.area - (1 + 2 * PLAYER_RADIUS) ** 2) < 0.5, `面积约等于膨胀后的方块（${fp.area.toFixed(2)} m²）`);
});

test('footprint: steps, kerbs and paving below the knee are walked over; roofs and lintels above the head are walked under', () => {
  assert.equal(footprintOf(group(box(4, 0.3, 4, 0, 0.15, 0))), null, '0.3 米高的台阶不挡');
  assert.equal(footprintOf(group(box(4, 0.02, 4, 0, 0.01, 0))), null, '铺地不挡');
  assert.equal(footprintOf(group(box(4, 0.3, 4, 0, 3, 0))), null, '只有屋顶、没有墙：什么都不挡');
  const gate = group(box(1, 4, 1, -2, 2, 0), box(1, 4, 1, 2, 2, 0), box(5.5, 0.6, 1.2, 0, 4.4, 0));
  const fp = footprintOf(gate);
  assert.ok(blockedAt(fp, -2, 0) && blockedAt(fp, 2, 0), '两根门柱挡');
  assert.ok(!blockedAt(fp, 0, 0), '过梁下面能走过去');
});

test('footprint: a closed room is filled in (doors are panels, not holes); a gap in the wall opens it', () => {
  const walls = (gap) => {
    const g = group(box(6, 3, 0.3, 0, 1.5, -3), box(0.3, 3, 6, -3, 1.5, 0), box(0.3, 3, 6, 3, 1.5, 0));
    if (gap) { g.add(box(2, 3, 0.3, -2, 1.5, 3)); g.add(box(2, 3, 0.3, 2, 1.5, 3)); } else g.add(box(6, 3, 0.3, 0, 1.5, 3));
    return g;
  };
  const closed = footprintOf(walls(false));
  assert.ok(blockedAt(closed, 0, 0), '四面墙围死的屋里是填实的');
  assert.ok(!blockedAt(closed, 0, 4.5), '屋外能站');
  const open = footprintOf(walls(true));
  assert.ok(!blockedAt(open, 0, 0), '前墙留了 2 米的门洞，院子里能进');
  assert.ok(!blockedAt(open, 0, 3), '门洞本身（2 米宽，减去两边各一个人的半径）能过');
});

test(`footprint: under an eave you can step in ${COVER_REACH} m; deeper under the roof is "inside"`, () => {
  // 一座敞开的工棚：一面后墙，屋顶向前伸出 6 米（檐口在 z = 4.5）
  const shed = group(box(8, 3, 0.4, 0, 1.5, -1.5), box(8, 0.2, 6, 0, 3.2, 1.5));
  const fp = footprintOf(shed);
  assert.ok(!blockedAt(fp, 0, 4.2), '檐口下第一步能站');
  assert.ok(!blockedAt(fp, 0, 4.5 - COVER_REACH + 0.3), `檐口之内 ${COVER_REACH - 0.3} 米能站`);
  assert.ok(blockedAt(fp, 0, 4.5 - COVER_REACH - 0.6), `檐口之内 ${COVER_REACH + 0.6} 米是屋里`);
  const canopy = footprintOf(shed, { coverReach: null });
  assert.ok(!blockedAt(canopy, 0, 4.5 - COVER_REACH - 0.6), '不按屋顶算（树冠）时整片檐下都能站');
});

test('footprint: a tree blocks its trunk, not its crown; things under 0.4 m do not block at all', () => {
  const trunk = box(0.3, 2, 0.3, 0, 1, 0);
  const crown = new THREE.Mesh(new THREE.SphereGeometry(1.6, 12, 8), mat);
  crown.position.y = 3.5;   // 冠底 1.9 米：成树的叶团最低在 1.66 米（plants.mjs），都在头顶之上
  const tree = footprintOf(group(trunk, crown), { coverReach: null, minHeight: BLOCKING_HEIGHT });
  assert.ok(blockedAt(tree, 0, 0), '树干挡');
  assert.ok(!blockedAt(tree, 1.0, 0), '树冠下面能走（1 米外、半径 1.6 的冠之下）');
  assert.equal(footprintOf(group(box(0.3, 0.38, 0.3, 0, 0.19, 0)), { minHeight: BLOCKING_HEIGHT }), null, '0.38 米的酒壶不挡');
  assert.ok(footprintOf(group(box(0.3, 0.42, 0.3, 0, 0.21, 0)), { minHeight: BLOCKING_HEIGHT }), '0.42 米的篮子挡');
  assert.ok(BODY_BAND.min < BLOCKING_HEIGHT, '高度带的下沿低于挡路的门槛，否则 0.4 米的东西虽算挡路却没有一个三角形进高度带');
});

test('footprint: hidden children, instanced meshes and nested assets', () => {
  const g = group(box(1, 1, 1, 0, 0.5, 0));
  const hidden = box(1, 1, 1, 3, 0.5, 0);
  hidden.visible = false;
  g.add(hidden);
  const fp = footprintOf(g);
  assert.ok(blockedAt(fp, 0, 0) && !blockedAt(fp, 3, 0), '隐藏的东西不算');

  const inst = new THREE.InstancedMesh(new THREE.BoxGeometry(0.4, 2, 0.4), mat, 2);
  inst.setMatrixAt(0, new THREE.Matrix4().makeTranslation(-2, 1, 0));
  inst.setMatrixAt(1, new THREE.Matrix4().makeTranslation(2, 1, 0));
  const cols = footprintOf(group(inst));
  assert.ok(blockedAt(cols, -2, 0) && blockedAt(cols, 2, 0) && !blockedAt(cols, 0, 0), '实例化的两根柱子各自挡、中间能走');

  const nested = group(box(1, 1, 1, 0, 0.5, 0));
  const tree = group(box(0.3, 2, 0.3, 4, 1, 0));
  tree.userData.asset = { id: 'plant.x', kind: 'plant', collision: 'band' };
  nested.add(tree);
  const own = footprintOf(nested, { stopAt: (o) => !!o.userData.asset });
  assert.ok(!blockedAt(own, 4, 0), '嵌在里面的另一件资产不算在这一件的足迹里（它自己盖）');
});

test('occupancy: footprints are stamped with the placement (rotation, scale), and the query is a point', () => {
  const grid = createOccupancy({ minX: -10, maxX: 10, minZ: -10, maxZ: 10 });
  const bar = group(box(3, 1, 0.4, 0, 0.5, 0));
  const fp = footprintOf(bar);
  const m = new THREE.Matrix4().compose(new THREE.Vector3(2, 0, 3), new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), Math.PI / 2), new THREE.Vector3(1, 1, 1));
  grid.stamp(fp, m);
  assert.ok(grid.blocked(2, 3), '中心');
  assert.ok(grid.blocked(2, 4.2) && grid.blocked(2, 1.8), '转了 90°：长边沿 z');
  assert.ok(!grid.blocked(3.4, 3) && !grid.blocked(0.6, 3), '短边沿 x 之外是空的');
  assert.ok(!grid.blocked(-5, -5) && !grid.blocked(50, 50), '别处与栅格之外都不挡');
  grid.clear();
  assert.ok(!grid.blocked(2, 3), '清掉之后能站');
  grid.stamp(fp, new THREE.Matrix4().makeScale(2, 2, 2));
  assert.ok(grid.blocked(2.8, 0) && !grid.blocked(3.8, 0), '按 2 倍缩放盖上去：长边伸到 3 米');
  assert.equal(grid.cell, CELL);
});

test('occupancy: footprintOfGeometry serves instanced grove trunks', () => {
  const fp = footprintOfGeometry(new THREE.CylinderGeometry(0.15, 0.2, 1.4, 8).translate(0, 0.7, 0), { minHeight: BLOCKING_HEIGHT, coverReach: null });
  assert.ok(fp && blockedAt(fp, 0, 0) && !blockedAt(fp, 0.8, 0));
});
