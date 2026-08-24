<script setup>
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { session } from '../session.js'
import { app } from './../app.js'
import PageShell from '../blocks/PageShell.vue'

// Where somebody lands.
//
// A generated application does not necessarily have a dashboard — whether it does is the
// definition's decision, not this file's — but it always has screens, and a home page that lists
// them is a better answer than a paragraph about how the application was produced. Which is what
// this was: three sentences about App Definitions and Apache licensing, shown to a person who
// wanted their tasks.
//
// This is a scaffold file. It is NOT regenerated, so it is yours to replace with whatever the
// business actually wants on the first screen.

const { t } = useI18n()

const greeting = computed(() => (session.displayName || session.email || '').split(/[\s@]/)[0])

const screens = computed(() => app.pages ?? [])
</script>

<template>
  <PageShell :title="t('app.name')" :subtitle="greeting ? `Signed in as ${greeting}.` : ''">
    <div v-if="screens.length" class="d-flex flex-wrap ga-4">
      <v-card
        v-for="page in screens"
        :key="page.key"
        :to="page.route"
        class="flex-grow-1"
        min-width="220"
        style="flex-basis: 240px; max-width: 340px"
      >
        <v-card-text class="d-flex align-center ga-3 pa-4">
          <v-avatar color="surface-light" size="40" rounded="md">
            <v-icon :icon="page.icon ? `mdi-${page.icon}` : 'mdi-file-outline'" size="20" />
          </v-avatar>
          <div style="min-width: 0">
            <div class="text-body-2 font-weight-medium text-truncate">{{ page.label }}</div>
            <div class="text-caption text-medium-emphasis text-truncate">{{ page.route }}</div>
          </div>
          <v-spacer />
          <v-icon icon="mdi-chevron-right" size="18" class="text-disabled" />
        </v-card-text>
      </v-card>
    </div>

    <!--
      Only for an application that genuinely has no screens yet — a scaffold nobody has generated
      into. Saying what this is beats a blank page.
    -->
    <v-card v-else>
      <v-card-text class="pa-6">
        <p class="text-body-2 mb-3">
          This application was generated from an App Definition, and this is the scaffold's home
          page. Screens generated from the definition appear in the navigation on the left.
        </p>
        <p class="text-body-2 text-medium-emphasis mb-0">
          Everything here is yours: ordinary ASP.NET Core, ordinary EF Core, ordinary Vue. Nothing
          calls home and nothing needs the tool that produced it.
        </p>
      </v-card-text>
    </v-card>
  </PageShell>
</template>
