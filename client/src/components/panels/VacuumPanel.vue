<script setup>
import { computed } from "vue";

import PanelSection from "@/components/ui/PanelSection.vue";
import { useEditor } from "@/stores/editor";
import { M_PER_FOOT, useSimulation } from "@/stores/simulation";

const sim = useSimulation();
const { state, speedMs, speedFts } = sim;
const { editor } = useEditor();

const displaySpeed = computed(() =>
  editor.unit === "ft/s" ? speedFts.value : speedMs.value
);

const range = computed(() => {
  const [lo, hi] = state.limits.speedMs;
  return editor.unit === "ft/s" ? [lo / M_PER_FOOT, hi / M_PER_FOOT] : [lo, hi];
});
</script>

<template>
  <PanelSection title="Vacuum">
    <label>
      Speed
      <span class="value">{{ displaySpeed.toFixed(2) }} {{ editor.unit }}</span>
      <input
        type="range"
        :min="range[0]"
        :max="range[1]"
        step="0.01"
        :value="displaySpeed"
        @input="sim.setSpeed($event.target.value, editor.unit)"
      />
    </label>

    <div class="row">
      <button :class="{ on: editor.unit === 'm/s' }" @click="editor.unit = 'm/s'">m/s</button>
      <button :class="{ on: editor.unit === 'ft/s' }" @click="editor.unit = 'ft/s'">ft/s</button>
      <span class="both">
        {{ speedMs.toFixed(2) }} m/s &middot; {{ speedFts.toFixed(2) }} ft/s
      </span>
    </div>

    <p v-if="state.pendingSpeed" class="pending">
      {{ state.pendingSpeed.toFixed(2) }} m/s queued - speed stays constant during a run.
    </p>
  </PanelSection>
</template>

<style scoped>
.both {
  margin-left: auto;
  font-size: 11px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}
</style>
