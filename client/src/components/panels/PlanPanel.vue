<script setup>
import PanelSection from "@/components/ui/PanelSection.vue";
import { useEditor } from "@/stores/editor";
import { useSimulation } from "@/stores/simulation";

const sim = useSimulation();
const { state } = sim;
const { editor, selected, selectedRoom, commitForm, deleteSelection } = useEditor();

function onCovering(event) {
  const value = event.target.value;
  sim.setRoomCovering(editor.selection.id, value === "" ? null : value);
}
</script>

<template>
  <PanelSection title="Floor plan">
    <div class="buttons">
      <button :class="{ on: editor.tool === 'select' }" @click="editor.tool = 'select'">
        Select
      </button>
      <button :class="{ on: editor.tool === 'draw_room' }" @click="editor.tool = 'draw_room'">
        Draw room
      </button>
    </div>

    <label class="check">
      <input v-model="editor.showGrid" type="checkbox" />
      Show grid
    </label>

    <div v-if="editor.selection && selected" class="inspector">
      <div class="head">
        <strong>{{ editor.selection.type === "room" ? "Room" : "Obstruction" }}</strong>
        <button class="link" @click="deleteSelection">Delete</button>
      </div>

      <label v-if="editor.selection.type === 'room'">
        Name
        <input v-model="editor.form.name" type="text" @change="commitForm" />
      </label>

      <label v-if="selectedRoom">
        Floor
        <select :value="selectedRoom.covering || ''" @change="onCovering">
          <option value="">Inherit house default</option>
          <option v-for="c in state.coverings" :key="c.name" :value="c.name">
            {{ c.label }}
          </option>
        </select>
      </label>

      <div class="size">
        <label>X <input v-model.number="editor.form.x" type="number" step="0.1" @change="commitForm" /></label>
        <label>Y <input v-model.number="editor.form.y" type="number" step="0.1" @change="commitForm" /></label>
      </div>
      <div class="size">
        <label>W <input v-model.number="editor.form.width" type="number" step="0.1" min="0.5" @change="commitForm" /></label>
        <label>H <input v-model.number="editor.form.height" type="number" step="0.1" min="0.5" @change="commitForm" /></label>
      </div>

      <p v-if="editor.selection.type === 'room'" class="pending">
        Editing rooms re-grids the house and restarts the run.
      </p>
    </div>
    <p v-else class="muted">Click a room or obstruction on the plan to edit it.</p>
  </PanelSection>
</template>

<style scoped>
.check {
  display: flex;
  align-items: center;
  gap: 7px;
  margin-top: 12px;
}

.check input {
  width: auto;
  margin: 0;
  accent-color: var(--accent);
}

.inspector {
  margin-top: 10px;
  padding: 10px;
  border: 1px solid var(--edge);
  border-radius: 7px;
  background: var(--panel-2);
}

.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.head strong {
  font-size: 12px;
}

.inspector label {
  margin-top: 8px;
}

button.link {
  flex: 0 0 auto;
  padding: 3px 8px;
  font-size: 11px;
  background: none;
  border-color: #5c3130;
  color: var(--bad);
}
</style>
