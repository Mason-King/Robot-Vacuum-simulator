<script setup>
import { computed } from "vue";

import { useSimulation } from "@/stores/simulation";

const { state } = useSimulation();

const tone = computed(() => {
  if (state.status === "running") return "running";
  if (state.status === "battery-empty") return "empty";
  if (state.status === "finished") return "finished";
  return "";
});
</script>

<template>
  <header>
    <h1>Robot Vacuum Simulator</h1>
    <div class="pills">
      <span class="pill" :class="tone">{{ state.status }}</span>
      <span class="pill" :class="state.connected ? 'up' : 'down'">
        {{ state.connected ? "connected" : "disconnected" }}
      </span>
    </div>
  </header>
</template>

<style scoped>
header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 9px 14px;
  border-bottom: 1px solid var(--edge);
  flex: none;
}

h1 {
  font-size: 14px;
  font-weight: 600;
}

.pills {
  display: flex;
  gap: 8px;
}

.pill {
  font-size: 11px;
  padding: 3px 9px;
  border-radius: 999px;
  border: 1px solid var(--edge);
  color: var(--muted);
}

.pill.up,
.pill.running {
  color: var(--good);
  border-color: #2f5c44;
}

.pill.down,
.pill.empty {
  color: var(--bad);
  border-color: #5c3130;
}

.pill.finished {
  color: var(--accent);
  border-color: #2c4a76;
}
</style>
