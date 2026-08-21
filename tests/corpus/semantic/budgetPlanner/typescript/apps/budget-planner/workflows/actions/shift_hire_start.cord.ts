import { defineAction } from "@cord/sdk";

export default defineAction({
  key: "shift_hire_start",
  icon: "calendar-arrow-right",
  input: {
    fields: ["start_month"],
    required: ["start_month"],
  },
  label: "Shift Start Month",
  entity: "hiring_line",
  effects: [
    {
      to: "{{actor.id}}",
      link: "auto",
      type: "notify",
      title: "Hire start moved: {{record.role_title}}",
      message: "{{record.role_title}} now starts {{record.start_month}}. Recalculate the scenario to refresh payroll and runway.",
    },
  ],
  description: "Move this hire's planned start month to protect runway.",
  successMessage: "Hire start month updated.",
} as const);
