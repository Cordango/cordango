import { defineDetail } from "@cord/sdk";

export default defineDetail({
  key: "segment",
  blocks: [
    {
      kind: "hub",
      subtitle: ["scenario"],
      actions: ["edit", "delete"],
      facts: ["mature_active_users", "mature_shared_apps", "avg_app_adoption", "setup_fee"],
    },
    {
      kind: "section",
      label: "At maturity",
      edit: ["mature_active_users", "mature_shared_apps", "avg_app_adoption", "setup_fee", "churn_pct"],
      blocks: [
        {
          kind: "fields",
          cols: 3,
          fields: ["mature_active_users", "mature_shared_apps", "avg_app_adoption", "setup_fee", "churn_pct"],
        },
      ],
    },
    {
      kind: "child",
      entity: "lifecycle_step",
      via: "segment",
      label: "Lifecycle",
      childType: "table",
      orderField: "age",
      fields: [
        "age",
        "active_users",
        "shared_apps",
        "active_per_app",
        "cheapest_plan_cost",
        "survival",
        "revenue_per_new_customer",
      ],
    },
    {
      kind: "section",
      label: "Notes",
      blocks: [
        {
          kind: "fields",
          cols: 1,
          fields: ["notes"],
        },
      ],
    },
  ],
} as const);
