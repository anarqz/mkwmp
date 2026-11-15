// Link stub for tests that #include npln.c (they define g_rt themselves): no COFF symbols.
#include <stddef.h>
void *coff_symbol(const char *name);
void *coff_symbol(const char *name) { (void)name; return NULL; }
