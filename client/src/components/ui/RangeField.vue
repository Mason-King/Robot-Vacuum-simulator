<script setup>
defineProps({
  label: { type: String, required: true },
  modelValue: { type: Number, required: true },
  min: { type: Number, default: 0 },
  max: { type: Number, default: 1 },
  step: { type: Number, default: 0.01 },
  display: { type: String, default: "" },
});

const emit = defineEmits(["update:modelValue"]);
</script>

<template>
  <label>
    {{ label }}
    <span class="value">{{ display || modelValue }}</span>
    <input
      type="range"
      :min="min"
      :max="max"
      :step="step"
      :value="modelValue"
      @input="emit('update:modelValue', Number($event.target.value))"
    />
  </label>
</template>
