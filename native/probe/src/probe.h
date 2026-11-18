// The start-up shared by both loaders: dllmain.c (winmm.dll proxy, COFF backend) and backend_plugin.c (mkw-recomp's
// plugin API). Each loader fills g_rt its own way between the two calls; everything else is here, so they cannot drift.
#ifndef MKWMP_PROBE_H
#define MKWMP_PROBE_H

// The session log (mkwmp.log in plat_home_path's folder), the version line, then <that folder>/mkwmp.env applied
// over the process environment. First thing a loader does (a plugin sets plat_set_home before).
void probe_log_start(void);

// g_rt is filled: register the probe's overrides (npln_register, which checks g_rt and mkwmp.toml / MKWMP_ENABLE).
// Returns npln_register's result: the override count, 0 inactive, -1 when the runtime does not fit.
int probe_register(void);

#endif
