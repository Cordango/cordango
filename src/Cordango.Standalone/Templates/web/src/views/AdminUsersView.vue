<script setup>
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '../api.js'
import { toast } from '../records.js'
import { session } from '../session.js'
import PageShell from '../blocks/PageShell.vue'
import EmptyState from '../blocks/EmptyState.vue'

// Who may sign in, and as what.
//
// This screen is why a generated application stops being a one-account application. Before it there
// was no endpoint to create a second account and no way to give anybody a role — and since the
// runtime reads the definition's role keys off the principal's role claims, an account without one
// reaches nothing at all. "Add a colleague" was not a hard task; it was an impossible one.

const { t } = useI18n()

const users = ref([])
const roles = ref([])
const people = ref([])
const loading = ref(true)
const error = ref(null)
const search = ref('')

const editing = ref(null)
const saving = ref(false)
const formError = ref(null)

const resetting = ref(null)
const resetPassword = ref('')

const confirming = ref(null)

const visible = computed(() => {
  const needle = search.value.trim().toLowerCase()
  if (!needle) return users.value
  return users.value.filter((u) =>
    [u.email, u.displayName, ...(u.roles || [])]
      .some((v) => String(v ?? '').toLowerCase().includes(needle)))
})

const peopleOptions = computed(() =>
  people.value.map((p) => ({ value: p.id, title: p.full_name || p.email || p.id })))

async function load() {
  loading.value = true
  error.value = null
  try {
    // The role list comes from the compiled definition, not from the identity tables. A role that
    // exists in the tables and not in the definition grants nothing, and offering it in a picker is
    // an invitation to spend an afternoon working out why.
    const [accounts, declared, directory] = await Promise.all([
      api.get('/api/admin/users'),
      api.get('/api/admin/roles'),
      api.get('/api/directory/person?take=500'),
    ])
    users.value = accounts ?? []
    roles.value = [declared.administrator, ...(declared.roles ?? []).map((r) => r.key)]
    people.value = directory?.items ?? []
  } catch (failure) {
    error.value = failure.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

function add() {
  editing.value = {
    id: null,
    email: '',
    displayName: '',
    password: '',
    personId: null,
    roles: [],
  }
  formError.value = null
}

function edit(user) {
  editing.value = { ...user, roles: [...(user.roles ?? [])], password: '' }
  formError.value = null
}

async function save() {
  saving.value = true
  formError.value = null
  const form = editing.value
  try {
    if (form.id) {
      await api.put(`/api/admin/users/${encodeURIComponent(form.id)}`, {
        displayName: form.displayName,
        personId: form.personId ?? '',
        roles: form.roles,
      })
    } else {
      await api.post('/api/admin/users', {
        email: form.email,
        password: form.password,
        displayName: form.displayName,
        personId: form.personId,
        roles: form.roles,
      })
    }
    editing.value = null
    toast(t('admin.saved'), 'success')
    await load()
  } catch (failure) {
    formError.value = failure.message
  } finally {
    saving.value = false
  }
}

async function act(promise, message) {
  try {
    await promise
    toast(message, 'success')
    await load()
  } catch (failure) {
    toast(failure.message, 'error')
  }
}

const toggleLock = (user) => act(
  api.post(`/api/admin/users/${encodeURIComponent(user.id)}/lock`, { locked: !user.locked }),
  user.locked ? t('admin.unlocked') : t('admin.locked'))

async function submitReset() {
  await act(
    api.post(`/api/admin/users/${encodeURIComponent(resetting.value.id)}/password`,
      { password: resetPassword.value }),
    t('admin.passwordReset'))
  resetting.value = null
  resetPassword.value = ''
}

const remove = (user) => act(
  api.delete(`/api/admin/users/${encodeURIComponent(user.id)}`),
  t('admin.deleted')).then(() => { confirming.value = null })

const isSelf = (user) => user.id === session.id
</script>

<template>
  <PageShell :title="t('admin.title')" :subtitle="t('admin.subtitle')">
    <template #actions>
      <v-btn color="primary" variant="flat" prepend-icon="mdi-account-plus-outline" @click="add">
        {{ t('admin.newUser') }}
      </v-btn>
    </template>

    <v-alert v-if="error" type="error">
      {{ error }}
      <template #append>
        <v-btn size="small" @click="load">{{ t('common.retry') }}</v-btn>
      </template>
    </v-alert>

    <v-card>
      <v-card-text class="pb-0">
        <v-text-field
          v-model="search"
          :placeholder="t('admin.search')"
          prepend-inner-icon="mdi-magnify"
          density="compact"
          clearable
          hide-details
          style="max-width: 340px"
        />
      </v-card-text>

      <v-skeleton-loader v-if="loading" type="table" />

      <div v-else-if="visible.length" class="cd-table-scroll mt-3">
        <v-table hover>
          <thead>
            <tr>
              <th>{{ t('admin.person') }}</th>
              <th>{{ t('admin.roles') }}</th>
              <th>{{ t('admin.status') }}</th>
              <th class="cd-row-actions" />
            </tr>
          </thead>
          <tbody>
            <tr v-for="user in visible" :key="user.id">
              <td>
                <div class="d-flex align-center ga-3 py-2">
                  <v-avatar :color="user.locked ? 'surface-light' : 'primary'" size="34">
                    <v-icon
                      :icon="user.locked ? 'mdi-lock-outline' : 'mdi-account'"
                      size="18"
                    />
                  </v-avatar>
                  <div>
                    <div class="text-body-2 font-weight-medium">
                      {{ user.displayName }}
                      <v-chip v-if="isSelf(user)" size="x-small" variant="tonal" class="ml-1">
                        {{ t('admin.you') }}
                      </v-chip>
                    </div>
                    <div class="text-caption text-medium-emphasis">{{ user.email }}</div>
                  </div>
                </div>
              </td>
              <td>
                <div class="d-flex flex-wrap ga-1">
                  <v-chip
                    v-for="role in user.roles"
                    :key="role"
                    size="x-small"
                    variant="tonal"
                    :color="role === 'Administrator' ? 'primary' : undefined"
                  >
                    {{ role }}
                  </v-chip>
                  <!-- Not decoration. An account with no role reaches nothing in this application,
                       and it looks exactly like an account that works. -->
                  <v-chip v-if="!user.roles.length" size="x-small" variant="tonal" color="warning">
                    {{ t('admin.noRoles') }}
                  </v-chip>
                </div>
              </td>
              <td>
                <v-chip v-if="user.locked" size="x-small" color="error" variant="tonal">
                  {{ t('admin.lockedLabel') }}
                </v-chip>
                <v-chip
                  v-else-if="user.mustChangePassword"
                  size="x-small"
                  color="warning"
                  variant="tonal"
                >
                  {{ t('admin.mustChange') }}
                </v-chip>
                <span v-else class="text-caption text-medium-emphasis">{{ t('admin.active') }}</span>
              </td>
              <td class="cd-row-actions text-right">
                <v-menu location="bottom end">
                  <template #activator="{ props }">
                    <v-btn icon="mdi-dots-horizontal" size="small" v-bind="props" />
                  </template>
                  <v-list>
                    <v-list-item
                      prepend-icon="mdi-pencil-outline"
                      :title="t('admin.edit')"
                      @click="edit(user)"
                    />
                    <v-list-item
                      prepend-icon="mdi-lock-reset"
                      :title="t('admin.resetPassword')"
                      @click="resetting = user; resetPassword = ''"
                    />
                    <v-list-item
                      :prepend-icon="user.locked ? 'mdi-lock-open-variant-outline' : 'mdi-lock-outline'"
                      :title="user.locked ? t('admin.unlock') : t('admin.lock')"
                      :disabled="isSelf(user) && !user.locked"
                      @click="toggleLock(user)"
                    />
                    <v-divider />
                    <v-list-item
                      prepend-icon="mdi-delete-outline"
                      :title="t('admin.delete')"
                      base-color="error"
                      :disabled="isSelf(user)"
                      @click="confirming = user"
                    />
                  </v-list>
                </v-menu>
              </td>
            </tr>
          </tbody>
        </v-table>
      </div>

      <EmptyState
        v-else
        icon="mdi-account-search-outline"
        :title="search ? t('admin.noMatches') : t('admin.none')"
      />
    </v-card>

    <!-- create / edit ------------------------------------------------------------------------ -->
    <v-dialog :model-value="!!editing" max-width="520" @update:model-value="editing = null">
      <v-card v-if="editing">
        <v-card-title>{{ editing.id ? t('admin.editUser') : t('admin.newUser') }}</v-card-title>
        <v-card-text class="d-flex flex-column ga-4">
          <v-text-field
            v-model="editing.email"
            :label="t('profile.email')"
            type="email"
            :disabled="!!editing.id"
            autocomplete="off"
          />
          <v-text-field v-model="editing.displayName" :label="t('profile.name')" />

          <template v-if="!editing.id">
            <v-text-field
              v-model="editing.password"
              :label="t('admin.initialPassword')"
              :hint="t('admin.initialPasswordHint')"
              persistent-hint
              type="text"
              autocomplete="off"
            />
          </template>

          <v-select
            v-model="editing.roles"
            :label="t('admin.roles')"
            :items="roles"
            :hint="t('admin.rolesHint')"
            persistent-hint
            multiple
            chips
          />

          <v-autocomplete
            v-model="editing.personId"
            :label="t('admin.linkedPerson')"
            :items="peopleOptions"
            :hint="t('admin.linkedPersonHint')"
            persistent-hint
            clearable
          />

          <v-alert v-if="formError" type="error">{{ formError }}</v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="editing = null">{{ t('common.cancel') }}</v-btn>
          <v-btn color="primary" variant="flat" :loading="saving" @click="save">
            {{ t('common.save') }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- reset ------------------------------------------------------------------------------- -->
    <v-dialog :model-value="!!resetting" max-width="460" @update:model-value="resetting = null">
      <v-card v-if="resetting">
        <v-card-title>{{ t('admin.resetPassword') }}</v-card-title>
        <v-card-text class="d-flex flex-column ga-4">
          <p class="text-body-2 text-medium-emphasis mb-0">
            {{ t('admin.resetBody', { name: resetting.displayName }) }}
          </p>
          <v-text-field v-model="resetPassword" :label="t('profile.new')" type="text" autocomplete="off" />
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="resetting = null">{{ t('common.cancel') }}</v-btn>
          <v-btn color="primary" variant="flat" :disabled="!resetPassword" @click="submitReset">
            {{ t('admin.resetPassword') }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- delete ------------------------------------------------------------------------------ -->
    <v-dialog :model-value="!!confirming" max-width="460" @update:model-value="confirming = null">
      <v-card v-if="confirming">
        <v-card-title>{{ t('admin.deleteTitle') }}</v-card-title>
        <v-card-text class="text-medium-emphasis">
          {{ t('admin.deleteBody', { name: confirming.displayName }) }}
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="confirming = null">{{ t('common.cancel') }}</v-btn>
          <v-btn color="error" variant="flat" @click="remove(confirming)">{{ t('admin.delete') }}</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </PageShell>
</template>
