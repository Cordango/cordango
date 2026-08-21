import { defineScreen } from "@cord/sdk";

export default defineScreen({
  key: "settings",
  label: "Planner Settings",
  icon: "cog-outline",
  subject: "budget_settings",
  navigationGroup: "config",
  layout: [
    {
      kind: "settings",
      entity: "budget_settings",
    },
  ],
} as const);
