<script setup>
import { onBeforeUnmount, onMounted, ref } from "vue";

import AppHeader from "@/components/AppHeader.vue";
import FloorPlan from "@/components/FloorPlan.vue";
import WarningDialog from "@/components/WarningDialog.vue";
import BatteryPanel from "@/components/panels/BatteryPanel.vue";
import CoveragePanel from "@/components/panels/CoveragePanel.vue";
import FloorPanel from "@/components/panels/FloorPanel.vue";
import ObstructionPanel from "@/components/panels/ObstructionPanel.vue";
import PhysicsPanel from "@/components/panels/PhysicsPanel.vue";
import PlanPanel from "@/components/panels/PlanPanel.vue";
import RunPanel from "@/components/panels/RunPanel.vue";
import VacuumPanel from "@/components/panels/VacuumPanel.vue";
import { useEditor } from "@/stores/editor";
import { useSimulation } from "@/stores/simulation";

const sim = useSimulation();
const { state, warningSignature } = sim;
const { editor } = useEditor();

const showWarnings = ref(false);

function acknowledgeAndStart() {
  editor.acknowledged = warningSignature.value;
  showWarnings.value = false;
  sim.start();
}

onMounted(sim.connect);
onBeforeUnmount(sim.disconnect);
</script>

<template>
  <AppHeader />

  <main>
    <FloorPlan />

    <aside>
      <div v-if="state.warnings.length" class="warnings">
        <strong>
          {{ state.warnings.length }} connectivity
          warning{{ state.warnings.length > 1 ? "s" : "" }}
        </strong>
        <p v-for="w in state.warnings" :key="w.code + w.room">{{ w.message }}</p>
      </div>
      <p v-if="state.notice" class="notice">{{ state.notice }}</p>

      <RunPanel @confirm-warnings="showWarnings = true" />
      <VacuumPanel />
      <PhysicsPanel />
      <BatteryPanel />
      <FloorPanel />
      <PlanPanel />
      <ObstructionPanel />
      <CoveragePanel />
    </aside>
  </main>

  <WarningDialog
    v-if="showWarnings"
    :warnings="state.warnings"
    @acknowledge="acknowledgeAndStart"
    @cancel="showWarnings = false"
  />
</template>

<style scoped>
main {
  flex: 1;
  display: flex;
  min-height: 0;
}

aside {
  width: 290px;
  flex: none;
  padding: 12px 14px 24px;
  border-left: 1px solid var(--edge);
  background: var(--panel);
  overflow-y: auto;
}

.warnings,
.notice {
  border-radius: 7px;
  padding: 9px 11px;
  margin-bottom: 12px;
  font-size: 12px;
}

.warnings {
  background: rgba(224, 167, 94, 0.12);
  border: 1px solid #6b4f2c;
}

.warnings p {
  margin: 6px 0 0;
  color: #f0d7b4;
}

.notice {
  background: rgba(77, 155, 255, 0.1);
  border: 1px solid #2c4a76;
  margin: 0 0 12px;
}
</style>
