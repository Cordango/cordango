// The application shell speaks English and German. What the DEFINITION says — entity labels, field
// labels, screen titles — passes through as authored: those are the words of the business, and
// translating them is a decision only the business can make.

export const messages = {
  en: {
    app: { name: '{{AppName}}' },
    nav: { home: 'Home', directory: 'Directory', signOut: 'Sign out' },
    auth: {
      title: 'Sign in',
      email: 'Email',
      password: 'Password',
      remember: 'Stay signed in',
      submit: 'Sign in',
      failed: 'That email address and password do not match.',
    },
    setup: {
      title: 'Create your administrator account',
      intro: 'This database is new. The first account is yours, and nothing else can happen until it exists — so nobody has to be handed a password they did not choose.',
      email: 'Email',
      displayName: 'Your name',
      displayNameHint: 'Optional. Shown to other people in this application.',
      password: 'Password',
      confirm: 'Repeat the password',
      submit: 'Create account and sign in',
      short: 'Use at least {count} characters.',
      mismatch: 'The two passwords do not match.',
      failed: 'The account could not be created.',
    },
    directory: {
      title: 'Directory',
      people: 'People',
      departments: 'Departments',
      organizations: 'Organizations',
      empty: 'Nobody here yet.',
    },
    common: { loading: 'Loading…', retry: 'Try again', save: 'Save', cancel: 'Cancel' },
  },
  de: {
    app: { name: '{{AppName}}' },
    nav: { home: 'Start', directory: 'Verzeichnis', signOut: 'Abmelden' },
    auth: {
      title: 'Anmelden',
      email: 'E-Mail',
      password: 'Passwort',
      remember: 'Angemeldet bleiben',
      submit: 'Anmelden',
      failed: 'E-Mail-Adresse und Passwort passen nicht zusammen.',
    },
    setup: {
      title: 'Administrationskonto anlegen',
      intro: 'Diese Datenbank ist neu. Das erste Konto ist Ihres, und vorher geht nichts — so bekommt niemand ein Passwort, das er sich nicht selbst ausgesucht hat.',
      email: 'E-Mail',
      displayName: 'Ihr Name',
      displayNameHint: 'Optional. Wird anderen Personen in dieser Anwendung angezeigt.',
      password: 'Passwort',
      confirm: 'Passwort wiederholen',
      submit: 'Konto anlegen und anmelden',
      short: 'Mindestens {count} Zeichen verwenden.',
      mismatch: 'Die beiden Passwörter stimmen nicht überein.',
      failed: 'Das Konto konnte nicht angelegt werden.',
    },
    directory: {
      title: 'Verzeichnis',
      people: 'Personen',
      departments: 'Abteilungen',
      organizations: 'Organisationen',
      empty: 'Hier ist noch niemand.',
    },
    common: { loading: 'Wird geladen…', retry: 'Erneut versuchen', save: 'Speichern', cancel: 'Abbrechen' },
  },
}
