import {
  INK_SPLAT_CAPACITY,
  INK_SPLAT_LIFETIME,
  InkFieldModel,
  sceneFromModel,
} from "./ink-field-model";

const FRAME = 1 / 60;

function advance(model: InkFieldModel, seconds: number, step = FRAME) {
  for (let elapsed = 0; elapsed < seconds; elapsed += step) {
    model.update(step);
  }
}

describe("InkFieldModel", () => {
  it("stays at rest without pointer input", () => {
    const model = new InkFieldModel();
    advance(model, 3);

    const snapshot = model.readSnapshot();

    expect(model.pointerActive).toBe(false);
    expect(snapshot.speed).toBe(0);
    expect(snapshot.energy).toBe(0);
    expect(snapshot.splatCount).toBe(0);
  });

  it("moves the fast matter closer to the pointer than the lagging body", () => {
    const model = new InkFieldModel();
    model.pointer(400, 300);
    model.pointer(900, 300);
    advance(model, 0.12);

    const snapshot = model.readSnapshot();
    const fastError = Math.abs(snapshot.fastX - 900);
    const slowError = Math.abs(snapshot.slowX - 900);

    expect(fastError).toBeLessThan(slowError);
    expect(fastError).toBeLessThan(60);
    expect(slowError).toBeGreaterThan(200);
  });

  it("raises energy while moving fast and decays it after idling", () => {
    const model = new InkFieldModel();
    model.pointer(100, 300);

    for (let x = 124; x <= 700; x += 24) {
      model.pointer(x, 300);
      model.update(FRAME);
    }

    expect(model.readSnapshot().energy).toBeGreaterThan(0.2);
    expect(model.readSnapshot().speed).toBeGreaterThan(200);

    advance(model, 3);
    expect(model.readSnapshot().energy).toBeLessThan(0.08);
  });

  it("returns the slow flow to rest later than the fast energy", () => {
    const model = new InkFieldModel();
    model.pointer(100, 300);

    for (let x = 124; x <= 700; x += 24) {
      model.pointer(x, 300);
      model.update(FRAME);
    }

    advance(model, 2);
    const snapshot = model.readSnapshot();

    expect(snapshot.flow).toBeGreaterThan(snapshot.energy);
  });

  it("emits splats along the movement, caps them and lets them expire", () => {
    const model = new InkFieldModel();
    model.pointer(0, 100);

    for (let x = 24; x <= 900; x += 24) {
      model.pointer(x, 100);
      model.update(FRAME);
    }

    expect(model.splatList.length).toBeGreaterThan(0);
    expect(model.splatList.length).toBeLessThanOrEqual(INK_SPLAT_CAPACITY);

    advance(model, INK_SPLAT_LIFETIME + FRAME);
    expect(model.splatList.length).toBe(0);
  });

  it("turns a pulse into a full-strength splat with an energy spike", () => {
    const model = new InkFieldModel();
    model.pulse(500, 400);
    model.update(FRAME);

    const splat = model.splatList.at(-1);

    expect(splat?.strength).toBe(1);
    expect(model.readSnapshot().energy).toBeGreaterThan(0.9);
  });
});

describe("sceneFromModel", () => {
  const viewport = { height: 500, width: 1000 };
  const calm = { strength: 0.72, x: 0.735, y: 0.5 };

  it("uses the rest pointer when there was no interaction", () => {
    const scene = sceneFromModel(new InkFieldModel(), 10, viewport, calm);

    expect(scene.pointerFast).toEqual(scene.pointerSlow);
    expect(scene.splatCount).toBe(0);
    expect(scene.splats).toHaveLength(INK_SPLAT_CAPACITY * 4);
    expect(scene.calm).toEqual(calm);
    expect(scene.energy).toBe(0);
  });

  it("converts pointer pixels and splats to flipped uv coordinates", () => {
    const model = new InkFieldModel();
    model.pointer(100, 200);

    for (let x = 124; x <= 300; x += 24) {
      model.pointer(x, 200);
      model.update(FRAME);
    }

    const scene = sceneFromModel(model, 10, viewport, calm);
    const count = scene.splatCount;

    expect(count).toBeGreaterThan(0);
    expect(scene.pointerFast[1]).toBeCloseTo(1 - 200 / 500, 1);
    expect(scene.pointerFast[0]).toBeGreaterThan(0.2);

    const lastSplatX = scene.splats[(count - 1) * 4];
    const lastSplatY = scene.splats[(count - 1) * 4 + 1];

    expect(lastSplatX).toBeGreaterThan(0.25);
    expect(lastSplatX).toBeLessThan(0.31);
    expect(lastSplatY).toBeCloseTo(1 - 200 / 500, 5);
  });
});
