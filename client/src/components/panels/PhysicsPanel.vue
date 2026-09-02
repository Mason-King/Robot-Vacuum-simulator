<script setup>
import { computed } from "vue";

import PanelSection from "@/components/ui/PanelSection.vue";
import RangeField from "@/components/ui/RangeField.vue";
import { useSimulation } from "@/stores/simulation";

const sim = useSimulation();
const { state } = sim;

const limits = computed(() => (state.vacuum ? state.vacuum.limits : null));
const wheels = computed(() => (state.vacuum ? state.vacuum.wheels : [0, 0]));

const turnRadius = computed(() => {
  if (!state.vacuum || state.vacuum.turnRadius === null) return "-";
  return `${Math.abs(state.vacuum.turnRadius).toFixed(2)} m`;
});

const surface = computed(() =>
  state.coverings.find((c) => c.name === state.floor.covering)
);
</script>

<template>
  <PanelSection v-if="limits" title="Chassis">
    <RangeField
      label="Acceleration"
      :model-value="limits.maxAccel"
      :min="state.limits.maxAccel[0]"
      :max="state.limits.maxAccel[1]"
      :step="0.05"
      :display="`${limits.maxAccel.toFixed(2)} m/s²`"
      @update:model-value="sim.setPhysics({ maxAccel: $event })"
    />
    <RangeField
      label="Max turn rate"
      :model-value="limits.maxTurnRate"
      :min="state.limits.maxTurnRate[0]"
      :max="state.limits.maxTurnRate[1]"
      :step="0.1"
      :display="`${limits.maxTurnRate.toFixed(1)} rad/s`"
      @update:model-value="sim.setPhysics({ maxTurnRate: $event })"
    />
    <RangeField
      label="Bumper damping"
      :model-value="limits.bumperDamping"
      :min="0"
      :max="1"
      :step="0.05"
      :display="`${(limits.bumperDamping * 100).toFixed(0)}%`"
      @update:model-value="sim.setPhysics({ bumperDamping: $event })"
    />

    <dl class="telemetry">
      <dt>Speed</dt>
      <dd>{{ state.vacuum.velocity.toFixed(2) }} m/s</dd>
      <dt>Turn</dt>
      <dd>{{ state.vacuum.angularVelocity.toFixed(2) }} rad/s</dd>
      <dt>Wheels L / R</dt>
      <dd>{{ wheels[0].toFixed(2) }} / {{ wheels[1].toFixed(2) }}</dd>
      <dt>Turn radius</dt>
      <dd>{{ turnRadius }}</dd>
      <dt>Motor load</dt>
      <dd>{{ (state.vacuum.motorLoad * 100).toFixed(0) }}%</dd>
      <dt>Traction</dt>
      <dd>{{ surface ? (surface.traction * 100).toFixed(0) + "%" : "-" }}</dd>
      <dt>Bumps</dt>
      <dd :class="{ hit: state.vacuum.touching }">{{ state.vacuum.bumps }}</dd>
    </dl>
  </PanelSection>
</template>

<style scoped>
.telemetry {
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--edge);
}

dd.hit {
  color: var(--bad);
}
</style>
