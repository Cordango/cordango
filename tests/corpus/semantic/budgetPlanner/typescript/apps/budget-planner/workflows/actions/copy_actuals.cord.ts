import { defineAction } from "@cord/sdk";

export default defineAction({
  key: "copy_actuals",
  icon: "content-copy",
  label: "Copy Plan to Actuals",
  entity: "period",
  effects: [
    {
      set: {
        actual_costs: "{{record.total_cost}}",
        actual_customers: "{{record.active_customers}}",
        actual_revenue: "{{record.revenue}}",
      },
      type: "updateRecord",
    },
  ],
  description: "Prefill this period's actuals with the planned figures so only the differences need editing.",
  successMessage: "Actuals prefilled from plan.",
} as const);
