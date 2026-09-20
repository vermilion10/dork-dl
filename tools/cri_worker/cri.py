# Gateway to PyCriCodecsEx. Always import CRI symbols from here so the GBK fallback applies.
# PyCriCodecsEx tries utf-8, shift-jis, utf-16; GBK is patched in as a last resort.
# USM is kept out of top-level imports so audio decoding doesn't require ffmpeg-python.

import PyCriCodecsEx.utf as _utf
from PyCriCodecsEx.awb import AWB
from PyCriCodecsEx.chunk import UTFType, UTFTypeValues
from PyCriCodecsEx.hca import HCA
from PyCriCodecsEx.utf import UTF

__all__ = ["UTF", "AWB", "HCA", "UTFType", "UTFTypeValues"]

_ORIGINAL_FALLBACK = ("shift-jis", "utf-16")
_PATCHED_FALLBACK = ("shift-jis", "utf-16", "gbk")


def _install_gbk_fallback() -> None:
    code = _utf.UTF._read_rows_and_columns.__code__
    if _PATCHED_FALLBACK in code.co_consts:
        return
    if _ORIGINAL_FALLBACK not in code.co_consts:
        return
    new_consts = tuple(
        _PATCHED_FALLBACK if c == _ORIGINAL_FALLBACK else c for c in code.co_consts
    )
    _utf.UTF._read_rows_and_columns.__code__ = code.replace(co_consts=new_consts)


_install_gbk_fallback()
