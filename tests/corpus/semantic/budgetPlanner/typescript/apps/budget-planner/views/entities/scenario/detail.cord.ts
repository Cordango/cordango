import { defineDetail } from "@cord/sdk";

export default defineDetail({
  key: "scenario",
  blocks: [
    {
      kind: "hub",
      facts: ["owner", "currency_code", "plan_years", "monthly_months", "starting_cash"],
      actions: ["clone_scenario", "mark_investor_ready", "edit", "delete"],
      subtitle: ["case_type", "plan_start"],
    },
    {
      kind: "process",
    },
    {
      kind: "tiles",
      tiles: [
        {
          icon: "cash",
          field: "total_revenue",
          label: "Plan Revenue",
          format: "money",
        },
        {
          icon: "cash-minus",
          field: "total_costs",
          label: "Plan Costs",
          format: "money",
        },
        {
          icon: "scale-balance",
          field: "net_result",
          label: "Net Result",
          format: "money",
        },
        {
          icon: "bank-transfer-in",
          field: "total_funding",
          label: "Funding",
          format: "money",
        },
        {
          icon: "wallet",
          field: "cash_at_end",
          label: "Cash at End",
          format: "money",
          attention: {
            op: "lt",
            value: 0,
          },
        },
        {
          icon: "timer-sand",
          field: "runway_months",
          label: "Runway (months)",
          format: "number",
          attention: {
            op: "lt",
            value: 6,
          },
        },
        {
          icon: "account-multiple",
          field: "headcount_end",
          label: "Planned Headcount",
          format: "number",
        },
      ],
    },
    {
      kind: "tabs",
      tabs: ["projection", "assumptions", "pricing", "hiring", "costs", "funding", "story", "activity"],
    },
  ],
} as const);
