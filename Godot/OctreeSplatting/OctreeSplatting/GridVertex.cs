// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public enum GridVertex {
        MinMinMin, MidMinMin, MaxMinMin,
        MinMidMin, MidMidMin, MaxMidMin,
        MinMaxMin, MidMaxMin, MaxMaxMin,
        
        MinMinMid, MidMinMid, MaxMinMid,
        MinMidMid, MidMidMid, MaxMidMid,
        MinMaxMid, MidMaxMid, MaxMaxMid,
        
        MinMinMax, MidMinMax, MaxMinMax,
        MinMidMax, MidMidMax, MaxMidMax,
        MinMaxMax, MidMaxMax, MaxMaxMax,
    }
}