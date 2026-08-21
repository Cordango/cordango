import { defineScreen } from "@cord/sdk";

export default defineScreen({
  key: "scenarios",
  label: "Scenarios",
  icon: "file-compare",
  subject: "scenario",
  navigationSource: {
    max: 12,
    sort: "case_type",
    labelField: "name",
  },
  detailFull: true,
  layout: [
    {
      kind: "tabs",
      tabs: [
        {
          key: "all",
          label: "All Scenarios",
          blocks: [
            {
              kind: "view",
              view: "scenarios_table",
            },
            {
              icon: "plus",
              kind: "create",
              label: "New Scenario",
              style: "primary",
              entity: "scenario",
            },
          ],
        },
        {
          key: "board",
          label: "By Stage",
          blocks: [
            {
              kind: "view",
              view: "scenarios_board",
            },
          ],
        },
      ],
    },
  ],
} as const);
