import { defineEntity } from "@cord/sdk";

export default defineEntity({
  key: "cost_category",
  icon: "shape-outline",
  kind: "config",
  label: "Cost Category",
  description: "Lookup list grouping cost lines (Infrastructure, Software, Government fees, Office, Marketing...).",
  plural: "Cost Categories",
  display: "name",
  fields: {
    name: {
      type: "text",
      label: "Category",
      unique: true,
      required: true,
    },
    cost_group: {
      type: "select",
      label: "Group",
      options: [
        {
          color: "#38bdf8",
          label: "Infrastructure & Hosting",
          value: "infrastructure",
        },
        {
          color: "#6366f1",
          label: "Software & Tools",
          value: "software",
        },
        {
          color: "#dc2626",
          label: "Government & Statutory Fees",
          value: "government",
        },
        {
          color: "#a16207",
          label: "Legal, Tax & Accounting",
          value: "professional",
        },
        {
          color: "#64748b",
          label: "Office & Equipment",
          value: "office",
        },
        {
          color: "#ec4899",
          label: "Marketing & Sales",
          value: "marketing",
        },
        {
          color: "#94a3b8",
          label: "Other",
          value: "other",
        },
      ],
      required: true,
    },
    is_opex: {
      type: "boolean",
      label: "Counts as OPEX",
      default: true,
    },
    description: {
      type: "longtext",
      label: "Description",
    },
  },
} as const);
