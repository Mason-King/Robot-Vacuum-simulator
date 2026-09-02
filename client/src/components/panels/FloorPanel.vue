<script setup>
import { computed } from "vue";

import PanelSection from "@/components/ui/PanelSection.vue";
import RangeField from "@/components/ui/RangeField.vue";
import { useEditor } from "@/stores/editor";
import { useSimulation } from "@/stores/simulation";

const sim = useSimulation();
const { state } = sim;
const { selectedRoom } = useEditor();

/* The efficiency slider edits whichever covering you are looking at: the
   selected room's, or the house default when nothing is selected. */
const editing = computed(() => {
  const room = selectedRoom.value;
  const name = (room && room.covering) || state.floor.covering;
  return state.coverings.find((c) => c.name === name) || null;
});

const efficiency = computed(() => {
  const map = state.floor.efficiencies || {};
  return editing.value ? (map[editing.value.name] ?? state.floor.efficiency) : 0;
});
</script>

<template>
  <PanelSection title="Floor covering">
    <label>
      House default
      <select
        :value="state.floor.covering"
        @change="sim.setFloorCovering($event.target.value)"
      >
        <option v-for="c in state.coverings" :key="c.name" :value="c.name">{{ c.label }}</option>
      </select>
    </label>
    <p class="muted">Used by every room that has no covering of its own.</p>

    <RangeField
      v-if="editing"
      :label="`${editing.label} efficiency`"
      :model-value="efficiency"
      :min="state.limits.efficiency[0]"
      :max="state.limits.efficiency[1]"
      :step="0.01"
      :display="`${(efficiency * 100).toFixed(0)}%`"
      @update:model-value="sim.setEfficiency($event, editing.name)"
    />
    <p v-if="selectedRoom" class="muted">
      Editing the covering under <strong>{{ selectedRoom.name }}</strong>.
    </p>

    <dl class="under">
      <dt>Under the vacuum</dt>
      <dd>{{ state.floor.underLabel || state.floor.label }}</dd>
      <dt>Traction</dt>
      <dd>{{ ((state.floor.traction ?? 1) * 100).toFixed(0) }}%</dd>
    </dl>
  </PanelSection>
</template>

<style scoped>
.under {
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--edge);
}

.muted strong {
  color: var(--text);
}
</style>
