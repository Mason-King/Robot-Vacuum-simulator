<script setup>
defineProps({ warnings: { type: Array, required: true } });
const emit = defineEmits(["acknowledge", "cancel"]);
</script>

<template>
  <div class="backdrop" @click.self="emit('cancel')">
    <div class="modal" role="dialog" aria-modal="true" aria-labelledby="warn-title">
      <h3 id="warn-title">Some floor cannot be reached</h3>
      <p class="lead">
        The connectivity check ran against the current layout and the vacuum's starting
        position. Starting anyway is allowed - the run will simply never touch this floor.
      </p>
      <ul>
        <li v-for="w in warnings" :key="w.code + w.room">{{ w.message }}</li>
      </ul>
      <div class="buttons">
        <button class="primary" @click="emit('acknowledge')">Acknowledge and start</button>
        <button @click="emit('cancel')">Cancel</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.backdrop {
  position: fixed;
  inset: 0;
  background: rgba(10, 12, 16, 0.72);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 10;
}

.modal {
  width: min(560px, 92vw);
  background: var(--panel);
  border: 1px solid var(--edge);
  border-radius: 10px;
  padding: 20px;
}

h3 {
  margin: 0 0 8px;
  font-size: 15px;
}

.lead {
  margin: 0 0 12px;
  color: var(--muted);
}

ul {
  margin: 0 0 16px;
  padding-left: 18px;
}

li {
  margin-bottom: 7px;
  color: #f0d7b4;
}
</style>
