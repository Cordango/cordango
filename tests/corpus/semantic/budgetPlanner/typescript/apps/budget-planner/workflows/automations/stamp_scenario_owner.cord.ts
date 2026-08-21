import { defineAutomation } from "@cord/sdk";

export default defineAutomation({
  key: "stamp_scenario_owner",
  name: "Stamp scenario owner on creation",
  trigger: "record.created",
  entity: "scenario",
  effects: [
    {
      set: {
        owner: "{{actor.id}}",
      },
      type: "updateRecord",
      setIfEmpty: true,
    },
  ],
} as const);
