<script setup>
import { useI18n } from 'vue-i18n'
import { session } from '../session.js'

const { t } = useI18n()
</script>

<template>
  <v-container>
    <h1 class="text-h4 mb-2">{{ t('app.name') }}</h1>
    <p class="text-medium-emphasis mb-6">
      Signed in as {{ session.displayName || session.email }}.
    </p>

    <!--
      Generated screens replace this card. It is here so that a freshly scaffolded application has
      somewhere to land rather than a blank page, and it says what it is instead of pretending to be
      a dashboard.
    -->
    <v-card variant="tonal" class="pa-4">
      <p class="mb-2">
        This application was generated from an App Definition, and this is the scaffold's home page.
        Screens generated from the definition appear in the navigation beside the directory.
      </p>
      <p class="text-medium-emphasis">
        Everything here is yours: ordinary ASP.NET Core, ordinary EF Core, ordinary Vue. Nothing
        calls home and nothing needs the tool that produced it.
      </p>
    </v-card>
  </v-container>
</template>
