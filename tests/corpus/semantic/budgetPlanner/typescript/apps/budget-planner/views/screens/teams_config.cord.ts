import { defineScreen } from "@cord/sdk";

export default defineScreen({
  key: "teams_config",
  label: "Teams",
  icon: "account-group",
  subject: "team",
  navigationGroup: "config",
  layout: [
    {
      kind: "view",
      view: "teams_table",
    },
  ],
} as const);
