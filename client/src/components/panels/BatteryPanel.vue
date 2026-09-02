<script setup>
import { computed } from "vue";

import PanelSection from "@/components/ui/PanelSection.vue";
import RangeField from "@/components/ui/RangeField.vue";
import { useSimulation } from "@/stores/simulation";

const sim = useSimulation();
const { state } = sim;

const color = computed(() => {
  if (state.battery.level > 40) return "var(--good)";
  return state.battery.level > 15 ? "var(--warn)" : "var(--bad)";
});

const capacity = computed(() => Math.round(state.battery.capacityMinutes));
</script>

<template>
  <PanelSection title="Battery">
    <div class="battery">
      <div class="meter">
        <i :style="{ width: state.battery.level + '%', background: color }" />
      </div>
      <span class="value">{{ state.battery.level.toFixed(0) }}%</span>
    </div>

    <RangeField
      label="Capacity"
      :model-value="capacity"
      :min="state.limits.capacityMinutes[0]"
      :max="Math.min(240, state.limits.capacityMinutes[1])"
      :step="1"
      :display="`${capacity} min`"
      @update:model-value="sim.setBatteryCapacity($event)"
    />

    <button class="wide" :disabled="!state.connected" @click="sim.recharge">Recharge</button>
  </PanelSection>
</template>

<style scoped>
.battery {
  display: flex;
  align-items: center;
  gap: 8px;
}

.meter {
  flex: 1;
  height: 9px;
  background: var(--panel-2);
  border-radius: 999px;
  overflow: hidden;
}

.meter i {
  display: block;
  height: 100%;
  transition: width 0.2s linear;
}

.value {
  font-variant-numeric: tabular-nums;
  font-size: 12px;
}
</style>
