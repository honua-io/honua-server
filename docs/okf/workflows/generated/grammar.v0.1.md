---
type: reference
title: Generated workflow grammar
---

# Generated workflow grammar

Do not edit: generated from grammar.v0.1.json. All executors require qualification.

## platform.verify

Requires: . Produces: platform. Executor: unavailable.

## platform.install

Requires: . Produces: platform. Executor: unavailable.

## connection.create

Requires: platform. Produces: connection. Executor: unavailable.

## dataset.import

Requires: platform. Produces: dataset. Executor: unavailable.

## dataset.register

Requires: connection. Produces: dataset. Executor: unavailable.

## service.publish

Requires: dataset. Produces: service, layer. Executor: unavailable.

## layer.publish

Requires: connection, dataset. Produces: layer. Executor: unavailable.

## layer.configure

Requires: layer. Produces: layer. Executor: unavailable.

## style.apply

Requires: layer. Produces: style. Executor: unavailable.

## map.compose

Requires: layer. Produces: map. Executor: unavailable.

## map.save

Requires: map. Produces: saved-version. Executor: unavailable.

## publication.propose

Requires: saved-version. Produces: proposal. Executor: unavailable.

## publication.approve

Requires: proposal. Produces: publication. Executor: unavailable.
