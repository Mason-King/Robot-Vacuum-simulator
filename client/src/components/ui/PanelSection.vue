<script setup>
defineProps({ title: { type: String, required: true } });
</script>

<template>
  <section>
    <h2>{{ title }}</h2>
    <slot />
  </section>
</template>

<style scoped>
section {
  padding-bottom: 14px;
  margin-bottom: 14px;
  border-bottom: 1px solid var(--edge);
}

section:last-child {
  border-bottom: none;
  margin-bottom: 0;
}

h2 {
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.07em;
  color: var(--muted);
  margin-bottom: 10px;
}
</style>
