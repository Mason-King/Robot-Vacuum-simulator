<script setup>
import PanelSection from "@/components/ui/PanelSection.vue";
import { useEditor } from "@/stores/editor";

const { editor } = useEditor();

function togglePlacing() {
  editor.tool = editor.tool === "place_obstruction" ? "select" : "place_obstruction";
}
</script>

<template>
  <PanelSection title="Obstructions">
    <div class="row">
      <button
        :class="{ on: editor.obstruction.kind === 'blocking' }"
        @click="editor.obstruction.kind = 'blocking'"
      >
        Blocking
      </button>
      <button
        :class="{ on: editor.obstruction.kind === 'pass_under' }"
        @click="editor.obstruction.kind = 'pass_under'"
      >
        Pass-under
      </button>
    </div>

    <div class="size">
      <label>
        Width
        <input v-model.number="editor.obstruction.width" type="number" min="0.2" max="4" step="0.1" />
      </label>
      <label>
        Height
        <input v-model.number="editor.obstruction.height" type="number" min="0.2" max="4" step="0.1" />
      </label>
    </div>

    <button class="wide" :class="{ on: editor.tool === 'place_obstruction' }" @click="togglePlacing">
      {{ editor.tool === "place_obstruction" ? "Click the plan to place..." : "Place obstruction" }}
    </button>
  </PanelSection>
</template>
