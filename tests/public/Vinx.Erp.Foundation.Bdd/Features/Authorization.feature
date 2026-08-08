@ignore
Feature: Explicit company context for commands
  The authorization boundary protects each company even when a caller forges a request.

  Scenario: A payment approval command without a company context is denied and audited
    Given Ana is authenticated with the payment.approve capability in company "Vinx Obras"
    When she prepares a payment approval without an empresa_id
    Then the command is denied
    And the authorization denial is auditable with Ana as the actor
