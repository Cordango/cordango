import { defineWorkspace } from "@cord/sdk";

export default defineWorkspace({
  formatVersion: 1,
  workspaceId: "semantic-budget-planner-sample",
  name: "Budget Planner semantic sample",
  runtime: ">=0.1 <0.2",
  coreApps: "default",
  apps: ["apps/budget-planner"],
} as const);
