/**
 * Estado de simulação do campo de tinta cobre. Mantém os filtros do ponteiro
 * (matéria próxima responde rápido; o corpo principal segue com atraso), a
 * resposta à velocidade e o ring buffer de splats dissipativos. É uma
 * estrutura pura em pixels CSS/segundos, sem dependência de DOM ou WebGL.
 */

export const INK_SPLAT_CAPACITY = 10;
export const INK_SPLAT_LIFETIME = 1.5;
export const INK_SPEED_REFERENCE = 900;

const POINTER_TAU_FAST = 0.05;
const POINTER_TAU_SLOW = 0.22;
const VELOCITY_TAU = 0.12;
const SPEED_TAU = 0.15;
const ENERGY_RISE_TAU = 0.09;
const ENERGY_DECAY_TAU = 0.9;
const FLOW_RISE_TAU = 0.6;
const FLOW_DECAY_TAU = 3.0;
const SPLAT_MIN_DISTANCE = 22;
const SPLAT_MIN_SPEED = 40;
const MAX_DELTA = 0.05;

export type InkSplat = {
  x: number;
  y: number;
  strength: number;
  age: number;
};

export type InkFieldViewport = {
  width: number;
  height: number;
};

export type InkFieldCalmZone = {
  x: number;
  y: number;
  strength: number;
};

export type InkFieldScene = {
  time: number;
  pointerFast: readonly [number, number];
  pointerSlow: readonly [number, number];
  velocity: readonly [number, number];
  speed: number;
  energy: number;
  flow: number;
  splats: Float32Array;
  splatCount: number;
  calm: InkFieldCalmZone;
};

const REST_POINTER = { x: 0.32, y: 0.55 } as const;

function smooth(value: number, target: number, tau: number, dt: number) {
  return value + (target - value) * (1 - Math.exp(-dt / tau));
}

export class InkFieldModel {
  private targetX = 0;
  private targetY = 0;
  private fastX = 0;
  private fastY = 0;
  private slowX = 0;
  private slowY = 0;
  private previousFastX = 0;
  private previousFastY = 0;
  private velocityX = 0;
  private velocityY = 0;
  private speed = 0;
  private energy = 0;
  private flow = 0;
  private lastEmitX: number | null = null;
  private lastEmitY: number | null = null;
  private hasPointer = false;
  private splats: InkSplat[] = [];

  pointer(x: number, y: number) {
    if (!this.hasPointer) {
      this.hasPointer = true;
      this.fastX = x;
      this.fastY = y;
      this.slowX = x;
      this.slowY = y;
      this.previousFastX = x;
      this.previousFastY = y;
      this.lastEmitX = x;
      this.lastEmitY = y;
    }

    this.targetX = x;
    this.targetY = y;
  }

  pulse(x: number, y: number) {
    this.pointer(x, y);
    this.energy = 1;
    this.emitSplat(1);
  }

  update(dt: number) {
    const step = Math.min(dt, MAX_DELTA);

    if (this.hasPointer) {
      this.previousFastX = this.fastX;
      this.previousFastY = this.fastY;
      this.fastX = smooth(this.fastX, this.targetX, POINTER_TAU_FAST, step);
      this.fastY = smooth(this.fastY, this.targetY, POINTER_TAU_FAST, step);
      this.slowX = smooth(this.slowX, this.targetX, POINTER_TAU_SLOW, step);
      this.slowY = smooth(this.slowY, this.targetY, POINTER_TAU_SLOW, step);

      const rawVelocityX = (this.fastX - this.previousFastX) / step;
      const rawVelocityY = (this.fastY - this.previousFastY) / step;
      this.velocityX = smooth(this.velocityX, rawVelocityX, VELOCITY_TAU, step);
      this.velocityY = smooth(this.velocityY, rawVelocityY, VELOCITY_TAU, step);

      const rawSpeed = Math.hypot(rawVelocityX, rawVelocityY);
      this.speed = smooth(this.speed, rawSpeed, SPEED_TAU, step);
    } else {
      this.velocityX = smooth(this.velocityX, 0, VELOCITY_TAU, step);
      this.velocityY = smooth(this.velocityY, 0, VELOCITY_TAU, step);
      this.speed = smooth(this.speed, 0, SPEED_TAU, step);
    }

    const speedTarget = Math.min(this.speed / INK_SPEED_REFERENCE, 1);
    this.energy = smooth(
      this.energy,
      speedTarget,
      speedTarget > this.energy ? ENERGY_RISE_TAU : ENERGY_DECAY_TAU,
      step,
    );
    this.flow = smooth(
      this.flow,
      speedTarget,
      speedTarget > this.flow ? FLOW_RISE_TAU : FLOW_DECAY_TAU,
      step,
    );

    for (const splat of this.splats) {
      splat.age += step;
    }

    this.splats = this.splats.filter((splat) => splat.age < INK_SPLAT_LIFETIME);

    if (
      this.hasPointer &&
      this.speed > SPLAT_MIN_SPEED &&
      this.lastEmitX !== null &&
      this.lastEmitY !== null &&
      Math.hypot(
        this.targetX - this.lastEmitX,
        this.targetY - this.lastEmitY,
      ) >= SPLAT_MIN_DISTANCE
    ) {
      this.emitSplat(
        Math.min(0.35 + (this.speed / INK_SPEED_REFERENCE) * 0.65, 1),
      );
      this.lastEmitX = this.targetX;
      this.lastEmitY = this.targetY;
    }
  }

  private emitSplat(strength: number) {
    this.splats.push({
      x: this.targetX,
      y: this.targetY,
      strength,
      age: 0,
    });

    if (this.splats.length > INK_SPLAT_CAPACITY) {
      this.splats.shift();
    }
  }

  get pointerActive() {
    return this.hasPointer;
  }

  get currentEnergy() {
    return this.energy;
  }

  get currentFlow() {
    return this.flow;
  }

  get splatList(): readonly InkSplat[] {
    return this.splats;
  }

  readSnapshot(): {
    fastX: number;
    fastY: number;
    slowX: number;
    slowY: number;
    velocityX: number;
    velocityY: number;
    speed: number;
    energy: number;
    flow: number;
    splatCount: number;
  } {
    return {
      fastX: this.fastX,
      fastY: this.fastY,
      slowX: this.slowX,
      slowY: this.slowY,
      velocityX: this.velocityX,
      velocityY: this.velocityY,
      speed: this.speed,
      energy: this.energy,
      flow: this.flow,
      splatCount: this.splats.length,
    };
  }
}

function pointerToUv(
  x: number,
  y: number,
  viewport: InkFieldViewport,
): readonly [number, number] {
  return [x / viewport.width, 1 - y / viewport.height];
}

export function sceneFromModel(
  model: InkFieldModel,
  time: number,
  viewport: InkFieldViewport,
  calm: InkFieldCalmZone,
): InkFieldScene {
  const snapshot = model.readSnapshot();
  const splats = new Float32Array(INK_SPLAT_CAPACITY * 4);
  const inkSplats = model.splatList;

  for (let index = 0; index < inkSplats.length; index += 1) {
    const splat = inkSplats[index];
    const offset = index * 4;
    splats[offset] = splat.x / viewport.width;
    splats[offset + 1] = 1 - splat.y / viewport.height;
    splats[offset + 2] = Math.min(splat.age / INK_SPLAT_LIFETIME, 1);
    splats[offset + 3] = splat.strength;
  }

  return {
    time,
    pointerFast: model.pointerActive
      ? pointerToUv(snapshot.fastX, snapshot.fastY, viewport)
      : [REST_POINTER.x, REST_POINTER.y],
    pointerSlow: model.pointerActive
      ? pointerToUv(snapshot.slowX, snapshot.slowY, viewport)
      : [REST_POINTER.x, REST_POINTER.y],
    velocity: [
      snapshot.velocityX / viewport.width,
      -snapshot.velocityY / viewport.height,
    ],
    speed: Math.min(snapshot.speed / INK_SPEED_REFERENCE, 1),
    energy: snapshot.energy,
    flow: snapshot.flow,
    splats,
    splatCount: inkSplats.length,
    calm,
  };
}
