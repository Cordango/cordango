import { defineRole } from "@cord/sdk";

export default defineRole({
  key: "admin",
  name: "Founder / Admin",
  description: "Full control over scenarios, plans, hiring, costs, funding and settings.",
  grants: {
    "*": {
      read: true,
      create: true,
      delete: true,
      update: true,
      commands: ["*"],
    },
  },
} as const);
