// Resolve mkw.exe runtime functions by name from its COFF symbol table.
#ifndef MKWMP_COFF_H
#define MKWMP_COFF_H

#include "recomp.h"

// Reads mkw.exe from disk (its own module path), parses the COFF symbol table, and fills g_rt by
// rebasing each symbol RVA onto the live module base. Returns 1 on success (all required symbols
// found), 0 otherwise. On failure, as much of g_rt as resolved is filled.
int coff_resolve_runtime(void);

#endif
