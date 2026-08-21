import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "segment",
  blocks: [
    {
      kind: "fields",
      columns: 2,
      fields: [
        "name",
        "scenario",
        "mature_active_users",
        "mature_shared_apps",
        "avg_app_adoption",
        "setup_fee",
        "churn_pct",
        "notes",
      ],
    },
  ],
} as const);
