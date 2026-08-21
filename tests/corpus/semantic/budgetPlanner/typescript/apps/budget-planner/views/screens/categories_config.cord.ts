import { defineScreen } from "@cord/sdk";

export default defineScreen({
  key: "categories_config",
  label: "Cost Categories",
  icon: "shape-outline",
  subject: "cost_category",
  navigationGroup: "config",
  layout: [
    {
      kind: "view",
      view: "categories_table",
    },
  ],
} as const);
