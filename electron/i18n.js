/* Manager/drawer translations. Keys are the Italian source strings written by
   manager.js; missing strings fall back to English, then to the source.
   English is the default until the user picks a language in Settings. */
(() => {
  const STORAGE_KEY = "infraorgit-language";
  const LANGUAGES = [
    { code: "en", label: "English" },
    { code: "it", label: "Italiano" },
    { code: "fr", label: "Français" },
    { code: "de", label: "Deutsch" },
    { code: "es", label: "Español" },
  ];
  const LOCALES = {
    en: "en-GB",
    it: "it-IT",
    fr: "fr-FR",
    de: "de-DE",
    es: "es-ES",
  };
  const TODAY = {
    en: "Today",
    it: "Oggi",
    fr: "Aujourd’hui",
    de: "Heute",
    es: "Hoy",
  };

  const en = {
    Istanze: "Instances",
    Verifica: "Check",
    "Verifica locale": "Local check",
    Offline: "Offline",
    "Dati locali": "Local data",
    "Da sincronizzare": "To sync",
    Credenziali: "Credentials",
    Scarica: "Download",
    Dimentica: "Forget",
    "Salvata offline.": "Saved offline.",
    "Solo in memoria.": "Memory only.",
    "Solo online.": "Online only.",
    "Consenti offline.": "Offline allowed.",
    "Istanza non raggiungibile: solo credenziali locali.":
      "Instance unreachable: local credentials only.",
    "Nessuna credenziale disponibile.": "No credentials available.",
    "Credenziale scaricata.": "Credential downloaded.",
    "Download non riuscito.": "Download failed.",
    Personale: "Personal",
    Condivisa: "Shared",
    Sincronizza: "Sync now",
    "Eventi sincronizzati.": "Events synced.",
    "Sincronizzazione non riuscita.": "Sync failed.",
    "Modalità offline: solo tunnel Client con chiave SSH nell’agente locale.":
      "Offline mode: Client tunnels only, with a key in the local SSH agent.",
    "Nessun dato locale disponibile. Riconnetti l’istanza InfraOrgIT per caricare i server.":
      "No local data available. Reconnect the InfraOrgIT instance to load the servers.",
    "Nessun server in cache": "No cached server",
    "Tipo verifica": "Check type",
    Origine: "Source",
    "Dal Mac": "From this Mac",
    "Via jump host": "Via jump host",
    Percorso: "Path",
    "Timeout (s)": "Timeout (s)",
    "Esegui verifica": "Run check",
    "Verifica in corso…": "Checking…",
    "Verifica non riuscita.": "Check failed.",
    "Il check via jump host usa il primo bastione della catena SSH e verifica solo TCP.":
      "The jump-host check uses the first bastion of the SSH chain and tests TCP only.",
    Istanza: "Instance",
    "Istanze aperte": "Open instances",
    "Nuova istanza": "New instance",
    "Aggiungi istanza": "Add instance",
    "Aggiungi un’installazione InfraOrgIT":
      "Add an InfraOrgIT installation",
    "Nessuna istanza configurata. Aggiungi la prima installazione per iniziare.":
      "No configured instances. Add the first installation to start.",
    Cerca: "Search",
    Tunnel: "Tunnels",
    "Tunnel attivi": "Active tunnels",
    "Tunnel attivo": "Active tunnel",
    "Tab aperta": "Open tab",
    "Tab aperte": "Open tabs",
    Sessione: "Session",
    Sessioni: "Sessions",
    Server: "Server",
    Connessioni: "Connections",
    "Ultimo uso": "Last used",
    Apri: "Open",
    Gestisci: "Manage",
    Rimuovi: "Remove",
    "Rimuovi istanza": "Remove instance",
    "Apri all’avvio": "Open at startup",
    "Apri connessione": "Open connection",
    "Nuova connessione": "New connection",
    "Scegli una connessione già configurata oppure creane una nuova.":
      "Choose a configured connection or create a new one.",
    "Istanze configurate o nuova connessione":
      "Configured instances or a new connection",
    Annulla: "Cancel",
    "Vista globale": "Global view",
    "Vista globale multi-istanza": "Multi-instance global view",
    "Tutte le istanze in un’unica vista. Il dettaglio del singolo server resta nella pagina":
      "All instances in one view. The individual server detail stays on the",
    "dell’istanza.": "page of the instance.",
    "Dettaglio del singolo server nella web app":
      "Individual server detail in the web app",
    "Cerca istanza, server o tunnel…": "Search instance, server or tunnel…",
    "Cerca tunnel": "Search tunnels",
    "Cerca istanze, tunnel, comandi…": "Search instances, tunnels, commands…",
    "Prova con un nome di istanza o tunnel": "Try an instance or tunnel name",
    "Gestisci istanza": "Manage instance",
    "Apri la pagina /tunnels dell’istanza": "Open the instance /tunnels page",
    "Accesso in corso": "Signing in",
    "Completa l’accesso nel browser e torna qui.":
      "Complete the sign-in in the browser and come back.",
    "Completa l’accesso nel browser.":
      "Complete the sign-in in the browser.",
    "Apri browser": "Open browser",
    "Accedi ai tunnel dell’istanza": "Sign in to the instance tunnels",
    "Sessione web attiva": "Web session active",
    "Accedi con Keycloak": "Sign in with Keycloak",
    "La sessione web resta attiva": "The web session stays active",
    "Pagina /tunnels": "/tunnels page",
    "Dimentica": "Forget",
    "Dimentica token": "Forget token",
    "Dimentica il refresh token salvato":
      "Forget the saved refresh token",
    "Esci dai tunnel": "Exit tunnels",
    "Nessun tunnel aperto per questa istanza.":
      "No open tunnel for this instance.",
    Anagrafica: "Details",
    "Apri anagrafica": "Open details",
    "Anagrafica istanza e servizi tunnel dei server":
      "Instance details and server tunnel services",
    "Apri i servizi tunnel del server nella web app":
      "Open the server tunnel services in the web app",
    Attivo: "Active",
    Fermo: "Stopped",
    "via server": "via server",
    diretto: "direct",
    "Apri nel browser": "Open in browser",
    "Apri l’istanza nel browser": "Open the instance in the browser",
    Ferma: "Stop",
    "Ferma il tunnel": "Stop the tunnel",
    Avvia: "Start",
    "Riavvia il tunnel": "Restart the tunnel",
    "Rimuovi dalla vista": "Remove from view",
    "Nuovo tunnel": "New tunnel",
    "Cerca macchina, ambiente o servizio":
      "Search machine, environment or service",
    "Server e servizio del tunnel": "Server and tunnel service",
    "Nessuna macchina o servizio corrisponde alla ricerca.":
      "No machine or service matches the search.",
    "Scegli un server o un servizio.": "Choose a server or a service.",
    "Nessun tunnel corrisponde alla ricerca.":
      "No tunnel matches the search.",
    "Nessun server visibile.": "No visible server.",
    "Caricamento dei server…": "Loading servers…",
    "Server non disponibili.": "Servers unavailable.",
    "Servizi tunnel dei server": "Server tunnel services",
    "Porta locale": "Local port",
    "Questo tunnel non espone un URL locale.":
      "This tunnel does not expose a local URL.",
    Impostazioni: "Settings",
    Aggiornamenti: "Updates",
    "Aggiornamenti e versione desktop": "Updates and desktop version",
    "Versione installata": "Installed version",
    "Ultima release": "Latest release",
    "Ultimo controllo": "Last check",
    "Nessun controllo eseguito.": "No check performed yet.",
    "Controlla aggiornamenti": "Check for updates",
    "Controllo completato.": "Check completed.",
    "Controllo non riuscito.": "Check failed.",
    "Installa e riavvia": "Install and restart",
    "Installare l'aggiornamento e riavviare l'app?":
      "Install the update and restart the app?",
    "Installazione avviata: l'app si riavvierà.":
      "Installation started: the app will restart.",
    "Installazione non riuscita.": "Installation failed.",
    "Apri release": "Open release",
    Chiudi: "Close",
    Lingua: "Language",
    "Lingua / Language": "Lingua / Language",
    "Aggiornamento…": "Updating…",
    "Controllo aggiornamenti…": "Checking for updates…",
    "Download del pacchetto…": "Downloading the package…",
    "Installazione e riavvio…": "Installing and restarting…",
    "Espandi istanza": "Expand instance",
    "Comprimi istanza": "Collapse instance",
    "Espandi server": "Expand server",
    "Comprimi server": "Collapse server",
    Locale: "Local",
    Dinamica: "Dynamic",
    Remota: "Remote",
    "Tunnel avviato verso": "Tunnel started towards",
    "Avvio del tunnel non riuscito.": "Tunnel start failed.",
    "Arresto del tunnel non riuscito.": "Tunnel stop failed.",
    "Riavvio del tunnel non riuscito.": "Tunnel restart failed.",
    "Rimozione del tunnel non riuscita.": "Tunnel removal failed.",
    "Accesso non riuscito.": "Sign-in failed.",
    "Tunnel non trovato.": "Tunnel not found.",
    "Tunnel non rimosso.": "Tunnel not removed.",
    "Tunnel aggiornati.": "Tunnels updated.",
    "Apertura dell’istanza non riuscita.": "Failed to open the instance.",
    "Comando non riuscito.": "Command failed.",
    "Operazione non riuscita.": "Operation failed.",
    "Rimozione non riuscita.": "Removal failed.",
    "Istanza aggiornata.": "Instance updated.",
    "Istanza rimossa.": "Instance removed.",
    "Nessun comando trovato.": "No command found.",
    "Mai aperta": "Never opened",
    Adesso: "Now",
    Completato: "Completed",
    "In corso": "In progress",
    "Non riuscito": "Failed",
    Annullato: "Cancelled",
    Riprova: "Retry",
    Salva: "Save",
    Aggiorna: "Refresh",
    "Chiudi scheda": "Close tab",
    "Ricarica istanza": "Reload instance",
    "Torna alla schermata principale": "Back to the main screen",
    "Apri drawer Tunnel": "Open Tunnel drawer",
    "Chiudi drawer Tunnel": "Close Tunnel drawer",
    "Apri la scheda server con la sezione Tunnel":
      "Open the server tab with the Tunnel section",
    "Nome istanza": "Instance name",
    Ambiente: "Environment",
    "Senza ambiente": "No environment",
    Servizio: "Service",
    Configurazione: "Configuration",
    Rimozione: "Removal",
    "Etichetta mostrata sulle card e nelle tab.":
      "Label shown on cards and tabs.",
    "L’URL identifica l’istanza e non si modifica da qui.":
      "The URL identifies the instance and cannot be changed here.",
    "Download attivi": "Active downloads",
    "Download attivo": "Active download",
    Download: "Download",
    "GitHub releases": "GitHub releases",
    "Device flow": "Device flow",
    "Keycloak device flow": "Keycloak device flow",
  };

  const fr = {
    Istanze: "Instances",
    Istanza: "Instance",
    "Nuova istanza": "Nouvelle instance",
    "Aggiungi istanza": "Ajouter une instance",
    Cerca: "Rechercher",
    Tunnel: "Tunnels",
    "Tunnel attivi": "Tunnels actifs",
    "Tunnel attivo": "Tunnel actif",
    "Tab aperta": "Onglet ouvert",
    "Tab aperte": "Onglets ouverts",
    Sessione: "Session",
    Sessioni: "Sessions",
    Server: "Serveur",
    Connessioni: "Connexions",
    "Ultimo uso": "Dernier usage",
    Apri: "Ouvrir",
    Gestisci: "Gérer",
    Rimuovi: "Supprimer",
    "Apri connessione": "Ouvrir une connexion",
    "Nuova connessione": "Nouvelle connexion",
    Annulla: "Annuler",
    "Vista globale": "Vue globale",
    "Vista globale multi-istanza": "Vue globale multi-instance",
    "Cerca istanza, server o tunnel…":
      "Rechercher une instance, un serveur ou un tunnel…",
    "Gestisci istanza": "Gérer l’instance",
    Attivo: "Actif",
    Fermo: "Arrêté",
    Ferma: "Arrêter",
    Avvia: "Démarrer",
    "Rimuovi dalla vista": "Retirer de la vue",
    "Nuovo tunnel": "Nouveau tunnel",
    "Cerca macchina, ambiente o servizio":
      "Rechercher une machine, un environnement ou un service",
    "Nessun tunnel corrisponde alla ricerca.":
      "Aucun tunnel ne correspond à la recherche.",
    Impostazioni: "Paramètres",
    Aggiornamenti: "Mises à jour",
    "Aggiornamenti e versione desktop":
      "Mises à jour et version du bureau",
    "Versione installata": "Version installée",
    "Ultima release": "Dernière version",
    "Ultimo controllo": "Dernière vérification",
    "Controlla aggiornamenti": "Vérifier les mises à jour",
    "Installa e riavvia": "Installer et redémarrer",
    "Apri release": "Ouvrir la version",
    Chiudi: "Fermer",
    Lingua: "Langue",
    "Lingua / Language": "Langue / Language",
    Locale: "Local",
    Dinamica: "Dynamique",
    Remota: "Distante",
    "Mai aperta": "Jamais ouverte",
    Adesso: "À l’instant",
    Completato: "Terminé",
    "In corso": "En cours",
    "Non riuscito": "Échoué",
    Annullato: "Annulé",
    Riprova: "Réessayer",
    Salva: "Enregistrer",
    Aggiorna: "Actualiser",
    "Chiudi scheda": "Fermer l’onglet",
    "Nome istanza": "Nom de l’instance",
    Ambiente: "Environnement",
    "Senza ambiente": "Sans environnement",
    Servizio: "Service",
    "Porta locale": "Port local",
  };
  const de = {
    Istanze: "Instanzen",
    Istanza: "Instanz",
    "Nuova istanza": "Neue Instanz",
    "Aggiungi istanza": "Instanz hinzufügen",
    Cerca: "Suchen",
    Tunnel: "Tunnel",
    "Tunnel attivi": "Aktive Tunnel",
    "Tunnel attivo": "Aktiver Tunnel",
    "Tab aperta": "Offener Tab",
    "Tab aperte": "Offene Tabs",
    Sessione: "Sitzung",
    Sessioni: "Sitzungen",
    Server: "Server",
    Connessioni: "Verbindungen",
    "Ultimo uso": "Zuletzt verwendet",
    Apri: "Öffnen",
    Gestisci: "Verwalten",
    Rimuovi: "Entfernen",
    "Apri connessione": "Verbindung öffnen",
    "Nuova connessione": "Neue Verbindung",
    Annulla: "Abbrechen",
    "Vista globale": "Globale Ansicht",
    "Vista globale multi-istanza": "Globale Multi-Instanz-Ansicht",
    "Cerca istanza, server o tunnel…": "Instanz, Server oder Tunnel suchen…",
    "Gestisci istanza": "Instanz verwalten",
    Attivo: "Aktiv",
    Fermo: "Gestoppt",
    Ferma: "Stoppen",
    Avvia: "Starten",
    "Rimuovi dalla vista": "Aus Ansicht entfernen",
    "Nuovo tunnel": "Neuer Tunnel",
    "Cerca macchina, ambiente o servizio":
      "Maschine, Umgebung oder Dienst suchen",
    "Nessun tunnel corrisponde alla ricerca.":
      "Kein Tunnel entspricht der Suche.",
    Impostazioni: "Einstellungen",
    Aggiornamenti: "Updates",
    "Aggiornamenti e versione desktop": "Updates und Desktop-Version",
    "Versione installata": "Installierte Version",
    "Ultima release": "Neueste Version",
    "Ultimo controllo": "Letzte Prüfung",
    "Controlla aggiornamenti": "Nach Updates suchen",
    "Installa e riavvia": "Installieren und neu starten",
    "Apri release": "Release öffnen",
    Chiudi: "Schließen",
    Lingua: "Sprache",
    "Lingua / Language": "Sprache / Language",
    Locale: "Lokal",
    Dinamica: "Dynamisch",
    Remota: "Entfernt",
    "Mai aperta": "Nie geöffnet",
    Adesso: "Jetzt",
    Completato: "Abgeschlossen",
    "In corso": "Läuft",
    "Non riuscito": "Fehlgeschlagen",
    Annullato: "Abgebrochen",
    Riprova: "Erneut versuchen",
    Salva: "Speichern",
    Aggiorna: "Aktualisieren",
    "Chiudi scheda": "Tab schließen",
    "Nome istanza": "Instanzname",
    Ambiente: "Umgebung",
    "Senza ambiente": "Ohne Umgebung",
    Servizio: "Dienst",
    "Porta locale": "Lokaler Port",
  };
  const es = {
    Istanze: "Instancias",
    Istanza: "Instancia",
    "Nuova istanza": "Nueva instancia",
    "Aggiungi istanza": "Añadir instancia",
    Cerca: "Buscar",
    Tunnel: "Túneles",
    "Tunnel attivi": "Túneles activos",
    "Tunnel attivo": "Túnel activo",
    "Tab aperta": "Pestaña abierta",
    "Tab aperte": "Pestañas abiertas",
    Sessione: "Sesión",
    Sessioni: "Sesiones",
    Server: "Servidor",
    Connessioni: "Conexiones",
    "Ultimo uso": "Último uso",
    Apri: "Abrir",
    Gestisci: "Gestionar",
    Rimuovi: "Eliminar",
    "Apri connessione": "Abrir conexión",
    "Nuova connessione": "Nueva conexión",
    Annulla: "Cancelar",
    "Vista globale": "Vista global",
    "Vista globale multi-istanza": "Vista global multiinstancia",
    "Cerca istanza, server o tunnel…": "Buscar instancia, servidor o túnel…",
    "Gestisci istanza": "Gestionar instancia",
    Attivo: "Activo",
    Fermo: "Detenido",
    Ferma: "Detener",
    Avvia: "Iniciar",
    "Rimuovi dalla vista": "Quitar de la vista",
    "Nuovo tunnel": "Nuevo túnel",
    "Cerca macchina, ambiente o servizio":
      "Buscar máquina, entorno o servicio",
    "Nessun tunnel corrisponde alla ricerca.":
      "Ningún túnel coincide con la búsqueda.",
    Impostazioni: "Ajustes",
    Aggiornamenti: "Actualizaciones",
    "Aggiornamenti e versione desktop":
      "Actualizaciones y versión de escritorio",
    "Versione installata": "Versión instalada",
    "Ultima release": "Última versión",
    "Ultimo controllo": "Última comprobación",
    "Controlla aggiornamenti": "Buscar actualizaciones",
    "Installa e riavvia": "Instalar y reiniciar",
    "Apri release": "Abrir versión",
    Chiudi: "Cerrar",
    Lingua: "Idioma",
    "Lingua / Language": "Idioma / Language",
    Locale: "Local",
    Dinamica: "Dinámica",
    Remota: "Remota",
    "Mai aperta": "Nunca abierta",
    Adesso: "Ahora",
    Completato: "Completado",
    "In corso": "En curso",
    "Non riuscito": "Fallido",
    Annullato: "Cancelado",
    Riprova: "Reintentar",
    Salva: "Guardar",
    Aggiorna: "Actualizar",
    "Chiudi scheda": "Cerrar pestaña",
    "Nome istanza": "Nombre de la instancia",
    Ambiente: "Entorno",
    "Senza ambiente": "Sin entorno",
    Servizio: "Servicio",
    "Porta locale": "Puerto local",
  };

  const dictionaries = { en, it: {}, fr, de, es };
  const connectionWords = {
    en: "Connections",
    it: "Connessioni",
    fr: "Connexions",
    de: "Verbindungen",
    es: "Conexiones",
  };
  const startedPrefix = {
    en: "Tunnel started towards",
    it: "Tunnel avviato verso",
    fr: "Tunnel démarré vers",
    de: "Tunnel gestartet zu",
    es: "Túnel iniciado hacia",
  };
  const minutesAgo = {
    en: (count) => `${count} min ago`,
    it: (count) => `${count} min fa`,
    fr: (count) => `il y a ${count} min`,
    de: (count) => `vor ${count} min`,
    es: (count) => `hace ${count} min`,
  };

  function resolveLanguage() {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (LANGUAGES.some((entry) => entry.code === stored)) return stored;
    } catch {
      // localStorage unavailable
    }
    return "en";
  }

  let language = resolveLanguage();

  function translateText(value, lang = language) {
    const text = String(value);
    const key = text.trim();
    if (!key || lang === "it") return value;
    const dictionary = dictionaries[lang] || {};
    const translated = dictionary[key] ?? en[key];
    if (translated) return text.replace(key, translated);
    const connection = /^Connessioni: (Server|Client|Auto)$/.exec(key);
    if (connection) return `${connectionWords[lang]}: ${connection[1]}`;
    const started = /^Tunnel avviato verso (.+)$/.exec(key);
    if (started) return `${startedPrefix[lang]} ${started[1]}`;
    const today = /^Oggi (.+)$/.exec(key);
    if (today) return `${TODAY[lang]} ${today[1]}`;
    const minutes = /^(\d+) min fa$/.exec(key);
    if (minutes) return minutesAgo[lang](minutes[1]);
    return value;
  }

  const attributes = [
    "placeholder",
    "title",
    "aria-label",
    "aria-description",
  ];

  function translateNode(node) {
    if (node.nodeType === Node.TEXT_NODE) {
      const next = translateText(node.nodeValue);
      if (next !== node.nodeValue) node.nodeValue = next;
      return;
    }
    if (node.nodeType !== Node.ELEMENT_NODE) return;
    for (const name of attributes) {
      if (!node.hasAttribute?.(name)) continue;
      const value = node.getAttribute(name);
      const next = translateText(value);
      if (next !== value) node.setAttribute(name, next);
    }
  }

  function walk(root) {
    translateNode(root);
    const walker = document.createTreeWalker(
      root,
      NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT,
    );
    let node = walker.nextNode();
    while (node) {
      translateNode(node);
      node = walker.nextNode();
    }
  }

  function start() {
    document.documentElement.lang = language;
    walk(document.body);
    const observer = new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        if (mutation.type === "characterData") translateNode(mutation.target);
        else for (const node of mutation.addedNodes) walk(node);
      }
    });
    observer.observe(document.body, {
      childList: true,
      subtree: true,
      characterData: true,
    });
  }

  function setLanguage(code) {
    if (!LANGUAGES.some((entry) => entry.code === code)) return;
    if (code === language) return;
    try {
      localStorage.setItem(STORAGE_KEY, code);
    } catch {
      // localStorage unavailable
    }
    location.reload();
  }

  // window.confirm bypasses the DOM, so translate its message here.
  const nativeConfirm = window.confirm.bind(window);
  window.confirm = (message) => nativeConfirm(translateText(message));

  window.desktopI18n = {
    start,
    setLanguage,
    translate: translateText,
    languages: LANGUAGES,
    locales: LOCALES,
    get language() {
      return language;
    },
    get locale() {
      return LOCALES[language];
    },
  };

  start();
})();
