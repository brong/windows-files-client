/*
 * Wrapper header that sets required preprocessor defines
 * before including the FUSE-T headers.
 */
#ifndef CFUSET_WRAPPER_H
#define CFUSET_WRAPPER_H

#define FUSE_USE_VERSION 26
#define _FILE_OFFSET_BITS 64

#include <fuse/fuse.h>

#endif /* CFUSET_WRAPPER_H */
