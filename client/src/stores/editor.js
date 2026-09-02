/* Floor-plan editing state: which tool is active, what is selected, and the
   numeric form bound to it. Shared because the canvas and the side panel are
   two views of the same selection. */

import { computed, reactive } from "vue";

import { useSimulation } from "./simulation";

const editor = reactive({
  tool: "select", // select | draw_room | place_obstruction
  selection: null, // { type: "room" | "obstruction", id }
  form: { name: "", x: 0, y: 0, width: 0, height: 0 },
  obstruction: { kind: "blocking", width: 0.8, height: 0.6 },
  unit: "m/s",
  showGrid: false,
  acknowledged: "",
});

export function useEditor() {
  const sim = useSimulation();

  const selectedRoom = computed(() => {
    if (!editor.selection || editor.selection.type !== "room" || !sim.state.house) {
      return null;
    }
    return sim.state.house.rooms.find((r) => r.name === editor.selection.id) || null;
  });

  const selectedObstruction = computed(() => {
    if (!editor.selection || editor.selection.type !== "obstruction") return null;
    return sim.state.obstructions.find((o) => o.id === editor.selection.id) || null;
  });

  const selected = computed(() => selectedRoom.value || selectedObstruction.value);

  function select(hit) {
    editor.selection = hit ? { type: hit.type, id: hit.id } : null;
    syncForm();
  }

  /** Pull the authoritative values from the engine into the numeric inputs. */
  function syncForm() {
    const target = selected.value;
    if (!target) {
      editor.form = { name: "", x: 0, y: 0, width: 0, height: 0 };
      return;
    }
    editor.form = {
      name: target.name || target.label || "",
      x: Number(target.x.toFixed(2)),
      y: Number(target.y.toFixed(2)),
      width: Number(target.width.toFixed(2)),
      height: Number(target.height.toFixed(2)),
    };
  }

  function commitForm() {
    const { form, selection } = editor;
    if (!selection) return;
    if (selection.type === "room") {
      sim.updateRoom({
        name: selection.id,
        newName: form.name !== selection.id ? form.name : undefined,
        x: form.x,
        y: form.y,
        width: form.width,
        height: form.height,
      });
      if (form.name) editor.selection = { type: "room", id: form.name };
    } else {
      sim.updateObstruction({
        id: selection.id,
        x: form.x,
        y: form.y,
        width: form.width,
        height: form.height,
      });
    }
  }

  function deleteSelection() {
    if (!editor.selection) return;
    if (editor.selection.type === "room") sim.removeRoom(editor.selection.id);
    else sim.removeObstruction(editor.selection.id);
    select(null);
  }

  return {
    editor,
    selected,
    selectedRoom,
    selectedObstruction,
    select,
    syncForm,
    commitForm,
    deleteSelection,
  };
}
