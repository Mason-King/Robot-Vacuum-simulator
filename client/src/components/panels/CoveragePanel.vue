<script setup>
import PanelSection from "@/components/ui/PanelSection.vue";
import { useSimulation } from "@/stores/simulation";

const { state, clock } = useSimulation();
</script>

<template>
  <PanelSection title="Coverage">
    <dl>
      <dt>Covered</dt>
      <dd>{{ state.coverage.percent.toFixed(1) }}%</dd>
      <dt>Fully cleaned</dt>
      <dd>{{ state.coverage.cleanedPercent.toFixed(1) }}%</dd>
      <dt>Mean cleanliness</dt>
      <dd>{{ state.coverage.meanCleanliness.toFixed(1) }}%</dd>
      <dt>Cleanable area</dt>
      <dd>{{ state.coverage.cleanableArea.toFixed(1) }} m&sup2;</dd>
      <dt>Blocked area</dt>
      <dd>{{ state.coverage.nonCleanableArea.toFixed(1) }} m&sup2;</dd>
      <dt>Sim clock</dt>
      <dd>{{ clock }}</dd>
      <dt>Travelled</dt>
      <dd>{{ state.vacuum ? state.vacuum.distanceTravelled.toFixed(1) : "0.0" }} m</dd>
    </dl>

    <div v-if="state.finalReport" class="report">
      <strong>Run ended ({{ state.finalReport.reason }})</strong>
      <p>
        Final coverage {{ state.finalReport.percent.toFixed(1) }}% of
        {{ state.finalReport.cleanableArea.toFixed(1) }} m&sup2; cleanable floor,
        {{ state.finalReport.cleanedPercent.toFixed(1) }}% fully cleaned, in
        {{ (state.finalReport.simTime / 60).toFixed(1) }} simulated minutes.
      </p>
    </div>
  </PanelSection>
</template>

<style scoped>
.report {
  background: rgba(70, 192, 122, 0.1);
  border: 1px solid #2f5c44;
  border-radius: 7px;
  padding: 9px 11px;
  margin-top: 12px;
  font-size: 12px;
}

.report p {
  margin: 5px 0 0;
}
</style>
