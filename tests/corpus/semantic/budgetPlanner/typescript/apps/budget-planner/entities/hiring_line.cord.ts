import { defineEntity } from "@cord/sdk";

export default defineEntity({
  key: "hiring_line",
  icon: "account-plus",
  kind: "collection",
  label: "Hiring Line",
  description: "Planned headcount for a team from a given start month, with salary and employer costs.",
  plural: "Hiring Plan",
  display: "role_title",
  fields: {
    role_title: {
      type: "text",
      label: "Role",
      required: true,
    },
    scenario: {
      type: "reference",
      label: "Scenario",
      indexed: true,
      onDelete: "cascade",
      required: true,
      targetEntity: "scenario",
    },
    team: {
      type: "reference",
      label: "Team",
      indexed: true,
      required: true,
      targetEntity: "team",
    },
    hire_status: {
      role: "status",
      type: "select",
      label: "Hiring Status",
      indexed: true,
    },
    headcount: {
      type: "integer",
      label: "Headcount",
      default: 1,
      required: true,
    },
    seniority: {
      type: "select",
      label: "Seniority",
      default: "mid",
      options: [
        {
          color: "#94a3b8",
          label: "Junior",
          value: "junior",
        },
        {
          color: "#38bdf8",
          label: "Mid",
          value: "mid",
        },
        {
          color: "#0f766e",
          label: "Senior",
          value: "senior",
        },
        {
          color: "#6366f1",
          label: "Lead / Head",
          value: "lead",
        },
        {
          color: "#f59e0b",
          label: "Executive",
          value: "exec",
        },
      ],
    },
    employment_type: {
      type: "select",
      label: "Employment Type",
      default: "full_time",
      options: [
        {
          label: "Full-time employee",
          value: "full_time",
        },
        {
          label: "Part-time employee",
          value: "part_time",
        },
        {
          label: "Contractor / Freelance",
          value: "contractor",
        },
        {
          label: "Working student (Werkstudent)",
          value: "working_student",
        },
      ],
    },
    start_month: {
      role: "start",
      type: "date",
      group: "Timing",
      label: "Planned Start",
      required: true,
    },
    end_month: {
      help: "Leave empty for an open-ended role.",
      type: "date",
      group: "Timing",
      label: "Planned End",
    },
    location_country: {
      type: "select",
      group: "Timing",
      label: "Location",
      default: "DE",
      options: [
        {
          label: "Germany",
          value: "DE",
        },
        {
          label: "Austria",
          value: "AT",
        },
        {
          label: "Switzerland",
          value: "CH",
        },
        {
          label: "Netherlands",
          value: "NL",
        },
        {
          label: "Poland",
          value: "PL",
        },
        {
          label: "Portugal",
          value: "PT",
        },
        {
          label: "Spain",
          value: "ES",
        },
        {
          label: "United States",
          value: "US",
        },
        {
          label: "United Kingdom",
          value: "GB",
        },
      ],
    },
    gross_salary: {
      type: "money",
      group: "Cost",
      label: "Gross Salary / Year (per head)",
      currency: "EUR",
      required: true,
    },
    employer_cost_rate: {
      help: "German employer social contributions on top of gross (approx. 20-22%).",
      type: "decimal",
      unit: "%",
      group: "Cost",
      label: "Employer Cost Rate",
      scale: 2,
      default: 21,
      precision: 6,
    },
    other_cost_per_head: {
      help: "Equipment, tooling, recruiting fee amortised per head per year.",
      type: "money",
      group: "Cost",
      label: "Other Cost per Head / Year",
      default: 0,
      currency: "EUR",
    },
    loaded_cost_per_head: {
      type: "money",
      group: "Cost",
      label: "Loaded Cost per Head / Year",
      currency: "EUR",
      calculate: {
        expression: "gross_salary * (1 + employer_cost_rate / 100) + other_cost_per_head",
      },
    },
    annual_cost: {
      type: "money",
      group: "Cost",
      label: "Annual Cost (all heads)",
      currency: "EUR",
      calculate: {
        expression: "headcount * (gross_salary * (1 + employer_cost_rate / 100) + other_cost_per_head)",
      },
    },
    monthly_cost: {
      type: "money",
      group: "Cost",
      label: "Monthly Cost (all heads)",
      currency: "EUR",
      calculate: {
        expression: "headcount * (gross_salary * (1 + employer_cost_rate / 100) + other_cost_per_head) / 12",
      },
    },
    notes: {
      type: "longtext",
      group: "Cost",
      label: "Notes",
    },
    setup_cost_per_head: {
      label: "One-off Cost per Head",
      type: "money",
      group: "Cost",
      currency: "EUR",
      default: 0,
      help: "Equipment, recruiter fee and onboarding — charged once, in the month the role starts.",
    },
    setup_cost_total: {
      label: "One-off Cost (all heads)",
      type: "money",
      group: "Cost",
      currency: "EUR",
      calculate: {
        expression: "headcount * setup_cost_per_head",
      },
    },
  },
} as const);
