<script setup>
import PanelSection from "@/components/ui/PanelSection.vue";
import RangeField from "@/components/ui/RangeField.vue";
import { useEditor } from "@/stores/editor";
import { useSimulation } from "@/stores/simulation";

const sim = useSimulation();
const { state, paramSchema, warningSignature } = sim;
const { editor } = useEditor();

const emit = defineEmits(["confirm-warnings"]);

function onStart() {
  // Unreachable floor needs an explicit acknowledgement first.
  if (state.warnings.length && warningSignature.value !== editor.acknowledged) {
    emit("confirm-warnings");
    return;
  }
  sim.start();
}

function onReset() {
  editor.acknowledged = "";
  sim.reset();
}

function onAlgorithmChange(event) {
  sim.setDrive(0, 0); // release any held WASD keys
  sim.setAlgorithm(event.target.value);
}

function paramValue(spec) {
  const value = state.algorithmParams[spec.name];
  return value === undefined ? spec.default : value;
}
</script>

<template>
  <PanelSection title="Run">
    <div class="buttons">
      <button class="primary" :disabled="!state.connected || state.running" @click="onStart">
        Start
      </button>
      <button :disabled="!state.connected || !state.running" @click="sim.stop">Stop</button>
      <button :disabled="!state.connected" @click="onReset">Reset</button>
    </div>

    <label>
      Algorithm
      <select :value="state.algorithm" @change="onAlgorithmChange">
        <option v-for="a in state.algorithms" :key="a.name" :value="a.name">
          {{ a.label }}
        </option>
      </select>
    </label>

    <div v-if="paramSchema.length" class="params">
      <RangeField
        v-for="spec in paramSchema"
        :key="spec.name"
        :label="spec.label"
        :model-value="paramValue(spec)"
        :min="spec.min"
        :max="spec.max"
        :step="spec.step"
        :display="`${paramValue(spec).toFixed(2)}${spec.unit ? ' ' + spec.unit : ''}`"
        @update:model-value="sim.setAlgorithmParams({ [spec.name]: $event })"
      />
    </div>

    <RangeField
      label="Time scale"
      :model-value="state.timeScale"
      :min="1"
      :max="20"
      :step="1"
      :display="`${state.timeScale}×`"
      @update:model-value="sim.setTimeScale($event)"
    />
  </PanelSection>
</template>

<style scoped>
.params {
  margin-top: 4px;
  padding: 9px 10px;
  border-radius: 7px;
  background: rgba(77, 155, 255, 0.06);
  border: 1px solid #2c3e57;
}

.params :deep(label:first-child) {
  margin-top: 0;
}
</style>
